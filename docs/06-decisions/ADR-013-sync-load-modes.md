# ADR-013 — The validation gate applies volume checks to full loads only
Status: Accepted, amended by ADR-024 (per-run mode, period-scoped baselines) · Date: 2026-09-02

## Context

`CLAUDE.md` rule 6 states that the validation gate "blocks row-count drops >50% vs last successful run", and `docs/03-architecture.md` §6.2 step 3 repeats it as "row count ≥ 50% of last success". Written that way the rule applies to every job.

It cannot. The jobs in §6.1 stage three different kinds of payload:

- **Full loads** re-fetch the entire current set. `LicenseSkuSync` stages every SKU the tenant holds, so a halving really does mean a truncated response, and the check is exactly right.
- **Incremental loads** stage only what changed. `UserAssignmentSync` (P1-3) uses `/users/delta`; a run that finds no changes stages zero rows against a live table of 90,000. The rule as written fails that run, and every other quiet run, for ever.
- **Append loads** add immutable facts for a period. `AzureCostSummarySync` and billed `TransactionSync` legitimately stage nothing when a period produced no new usage or no invoice was issued.

A gate that fails on ordinary, healthy runs does not make data safer. It trains whoever is on call to acknowledge the alert without reading it, and the one time it fires for a real truncation, it is ignored along with the rest.

## Decision

`SyncJobType` declares a `SyncLoadMode` — `Full`, `Incremental` or `Append` — and `SyncValidationGate` applies the volume checks (empty-response and >50% drop) to `Full` jobs only. Required-field and range validation applies to every mode, unchanged.

A job type not present in the mapping defaults to `Full`, so a new job added without declaring its mode gets the strictest gate rather than the weakest.

## Consequences

+ The gate keeps its meaning: when it fires on a full load, something is genuinely wrong.
+ Delta and append jobs can ship without either weakening the rule globally or special-casing it at each call site.
− Incremental jobs have weaker automatic protection. A delta feed that silently returns nothing forever would not be caught here. That is a monitoring concern — a per-tenant sync-health page showing time since last change is on the cross-cutting backlog — rather than something the gate can detect from row counts alone.
− `CLAUDE.md` rule 6 and `docs/03-architecture.md` §6.2 now understate the actual behaviour. They should be amended to reference this ADR; until they are, this document is authoritative.
