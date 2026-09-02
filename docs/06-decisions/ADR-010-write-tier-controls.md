# ADR-010 — Write operations are delegated-only and gated
Status: Accepted · Date: 2026-09-01

## Decision
Purchase, quantity change, cancellation, auto-renew toggle, and licence assignment run only under the signed-in user's delegated identity, require app role `SubscriptionManager`, a per-tenant feature flag, a pre-flight check of `systemOverrides`/`cancellationAllowedEndDate`, a confirmation showing financial impact, and an audit row written before the outbound call. App-only credentials never reach these code paths (enforced by test).
