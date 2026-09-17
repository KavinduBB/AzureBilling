# ADR-024 — Validation gate: per-run load mode, period-scoped baselines, staged-count baseline, audited override
Status: Accepted · Date: 2026-09-17 · Amends: ADR-013, CLAUDE.md rule 6, docs/03 §6.2

## Context

The review found four problems with the gate:

1. **The baseline is the wrong number.** It was `RecordsProcessed`, which is set to rows *affected by MERGE*. A quiet run lowers the baseline, and a truncated response on the next run then passes the gate and deletes live rows.
2. **The load mode is fixed per job type** (ADR-013), but `UserAssignmentSync` is incremental daily and a full load every 30 days.
3. **Period rollover looks like truncation.** `TransactionSync (unbilled)` legitimately drops to near zero when an invoice is issued, so the gate would block the first run of every period.
4. **A genuine large drop blocks the job forever.** There is no operator override.

## Decision

1. **Baseline column.** `SyncRun.StagedRowCount` holds the full staged count and is the only baseline. `RecordsProcessed` stays as rows merged, for telemetry.
2. **Per-run mode.** The load mode is chosen per run. `SyncJobType` declares a default. The scheduler or handler may request `Full` for a run (for example, the 30-day resync), and the mode is stored on `SyncRun.LoadMode`. The baseline is the last `Succeeded` run **with the same mode and the same `PeriodKey`**.
3. **Period-scoped jobs.**
   - Jobs whose data belongs to a billing period carry `SyncRun.PeriodKey` (for example `2026-09`, or the billing profile's current invoice period).
   - A run whose `PeriodKey` has no previous successful run is treated as a first run, so there is no volume check.
   - `TransactionSyncUnbilled` is `Full` within a period.
   - A drop of more than 50% within the **same** period is allowed only if `InvoiceSync` has recorded an invoice for that period since the baseline run (checked through `ISyncGateContext.InvoiceIssuedSince`). Otherwise it is blocked as usual.
4. **Override.** An operator (platform ops, not a customer) can record a one-shot `SyncGateOverride(TenantId, JobType, ExpiresUtc, Reason, ApprovedBy)`. The next run consumes it: it passes the volume check, is audited, and becomes the new baseline. Overrides expire after 24 h.
5. **Rule 6 wording.** Staging → validation gate (ADR-013/ADR-024) → single-transaction MERGE and run-status update → staging cleanup. The run's `Succeeded` status is written **in the same transaction** as the MERGE, and terminal states are final.
6. **Execution strategy.** The transaction runs inside `Database.CreateExecutionStrategy().ExecuteAsync(...)`, because EF's retrying strategy rejects user-initiated transactions. Every attempt re-stages nothing: staging is already durable, and the MERGE is idempotent on the natural key.
7. **Concurrency.**
   - A run acquires `sp_getapplock('sync:{tenant}:{job}', Exclusive, Session)` for its duration, so two replicas cannot run the same tenant/job. A second run completes as `Skipped`.
   - Runs left `Running` for more than 2 × the job's max duration are swept to `Abandoned`.
8. **Continuation.**
   - Handlers call `ISyncRunProgress.CheckpointAsync(token)` after each staged page.
   - A new run looks for the latest `Abandoned`/`Failed` run of the same (tenant, job, period) that has a continuation token and staging rows, and less than 24 h old. It adopts that run's staging, resumes from the token, and the old run is marked `Superseded`.
   - Staging is kept for resumable failures and swept after 24 h. It is cleared only on success or a gate rejection.

## Consequences

+ The gate can't ratchet itself down, handles billing-period rollover, and has a safe escape hatch.
+ Resume works as P0-8 requires.
− More columns on `SyncRun` (`LoadMode`, `PeriodKey`, `StagedRowCount`, `SupersededBySyncRunId`) and one new table (`SyncGateOverride`).
