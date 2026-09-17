# ADR-019 — Audit log: two rows per action, enforced append-only, purged with the tenant
Status: Accepted · Date: 2026-09-17 · Amends: docs/03 §8, docs/04 §9

## Context

The audit requirements conflict with each other:

- docs/03 §8 says the audit log is append-only and the database principal has only INSERT/SELECT.
- `CLAUDE.md` rule 12 requires an audit row *before* the outbound call, and the code then UPDATEs that row with the outcome.
- P0-11 requires deleting every tenant row.
- The published DPA (`/data-processing`) promises audit records "for the account lifetime", and that after deletion "we keep only a dated certificate … containing no personal data".

## Decision

1. **Two rows, never an update.**
   - `AuditLog` rows are immutable. An action that calls Microsoft writes an **Attempt** row (`Outcome = Pending`) before the call.
   - A second **Outcome** row (`Succeeded` / `Failed` / `Refused`) follows, carrying `AttemptAuditLogId`.
   - Actions with no outbound call write one row with its final outcome.
   - `AuditLog.RecordOutcome` (the mutating method) is removed.
2. **Enforced in the database.** A migration creates two database roles:
   - `mlcp_web`: SELECT/INSERT on `AuditLog`, and **DENY UPDATE, DELETE**.
   - `mlcp_worker`: SELECT/INSERT, plus DELETE, used only by tenant deletion.

   The web and worker identities are separate database users (ADR-026). An integration test impersonates `mlcp_web` (`EXECUTE AS USER`) and asserts that UPDATE and DELETE fail.
3. **Purged with the tenant**, matching the published DPA.
   - Audit rows are deleted in the same transaction as the other tenant data.
   - The **deletion certificate** is written in that same transaction. It records the row counts per table, including the audit count, and the SHA-256 of the canonicalised audit log taken just before purge. The digest proves what existed without keeping any personal data.
   - Microsoft's own activity logs remain the system of record for the underlying billing changes.
4. **Cascade removed.** The `AuditLog` → `Tenant` foreign key no longer cascades. Deletion is explicit and ordered in `TenantDeletionStore`, so an accidental tenant delete cannot silently remove audit history.

## Consequences

+ The append-only guarantee holds even against application bugs.
+ The deletion promise stays true, and the certificate is tamper-evident.
− Queries that show an action's status join Attempt → Outcome. `AuditQueries.WithOutcome()` encapsulates that.
