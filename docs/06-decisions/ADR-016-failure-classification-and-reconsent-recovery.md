# ADR-016 — Which failures mean "re-consent", and how a tenant recovers
Status: Accepted · Date: 2026-09-17 · Amends: CLAUDE.md rule 7, docs/03 §3 and §6.2, docs/05 P0-8

## Context

The two rule sets disagree:

- `CLAUDE.md` rule 7 and docs/03 §6.2 say any 401/403 marks the whole tenant `NeedsReconsent`.
- docs/02 §4 lists only lost-grant token errors.
- docs/03 §4.3 promises that "revoking a higher tier never breaks a lower one".

Applied literally, rule 7 flags every tier-1-only tenant, because the tier-2 usage probe returns 403. The same happens to any tenant without a billing role (403 on `billingRoleAssignments`) and any tenant with partial RBAC. Transient outages and failures of MLCP's own credential were also recorded as customer revocations, and nothing ever re-checked a flagged tenant.

## Decision

Every failure is classified once, in `Mlcp.Shared.Resilience`, into a `MicrosoftFailureKind`, and each kind has a fixed effect.

| Kind | Signals | Effect on tenant | Effect on capability | Retry |
|---|---|---|---|---|
| **GrantRevoked** | Token endpoint for the **core** app: `AADSTS700016` / `AADSTS7000229` (app or its service principal not in the tenant, i.e. the enterprise app was deleted) *after* the propagation window, `AADSTS7000112` (app disabled in the tenant), `AADSTS90002` (tenant not found), `invalid_grant`. **401** on a core Graph floor call made with a freshly acquired token. | → `NeedsReconsent` (reason recorded) | Floor capabilities → `ConsentRevoked` | No |
| **FloorPermissionRemoved** | **403** on a core Graph floor call (`subscribedSkus`, `directory/subscriptions`, `organization`) | → `NeedsReconsent` | `GraphLicensing` → `ConsentRevoked` | No |
| **CapabilityDenied** | 401/403 on any non-floor call: usage reports, ARM, Cost Management, Billing. Token-endpoint consent errors for the **Usage Insights** app. | None | That capability → `Tier2NotGranted` / `RbacMissing` / `BillingRoleMissing` (or `RoleRevoked` if it was previously available) | No |
| **Transient** | 408, 429, 5xx, timeouts, open circuit, network errors, token-endpoint 5xx or unreachable | None | **Previous verdict kept**, marked stale with `ProviderError` detail; no downgrade | Yes (Polly) |
| **PlatformCredential** | `AADSTS7000215`, `AADSTS7000222`, `AADSTS700027`, `invalid_client`, a Key Vault certificate load failure | None; **all** sync for that app is paused process-wide | None | Operator alert (`Critical` log event); retry after 5 min |
| **NotFound / Other 4xx** | 404, 400 | None | Probe-specific: e.g. no billing account → `NoBillingAccount` | No |

**Rules**

1. Probes use a non-throwing API (`MicrosoftApiClient.ProbeAsync`) that returns `(status, body)`. 401/403 is a normal probe answer. Each probe sub-result is captured independently, so one refusal never discards the others.
2. Only `GrantRevoked` and `FloorPermissionRemoved` change `Tenant.Status`. The flag is raised by the service that owns the tenant aggregate, on the same `DbContext`. The out-of-band signal that caused concurrency conflicts is removed.
3. **Propagation window.** `AADSTS700016` / `AADSTS7000229` within 10 minutes of `ConsentGrantedUtc` is `Transient`. Discovery after consent is re-queued with a 2-minute delay instead.
4. **Token cache invalidation.** On any 401/403 the cached token for that (tenant, app, audience) is evicted, so a newly granted role is picked up on the next attempt.
5. **Recovery.** `NeedsReconsent` tenants are re-probed automatically, with the floor probe only, at +1 h, +6 h, +24 h, then daily for 30 days.
   - If the probe succeeds, the tenant returns to `Active` with the audit action `ConsentRestored`. This covers consent restored in Entra without visiting MLCP, and false positives.
   - Owners also get a **Check again** button (rate-limited to once per 5 minutes).
   - After 30 days the tenant is still re-probed weekly, and the checklist shows the re-consent guide.
6. Circuit breakers stay per (tenant, provider). The concurrency limiter becomes per provider (docs/03 §6.3). `HttpClient.Timeout` is `InfiniteTimeSpan`, so Polly's per-attempt timeout is the only one.
7. Retry-After hints above a budget (default 60 s per call on interactive paths, 5 min in workers) fail fast and re-queue the job with a scheduled enqueue time instead of sleeping.
8. Retry hints are read from `Retry-After`, `x-ms-ratelimit-microsoft.consumption-retry-after` and `x-ms-ratelimit-microsoft.costmanagement-qpu-retry-after`.

`CLAUDE.md` rule 7 is reworded to reference this table.

## Consequences

+ A normal tier-1 tenant reaches `Active`. Partial grants degrade one capability, never the tenant.
+ An expired MLCP certificate pages the operator instead of making every customer re-consent.
+ False positives heal themselves.
− Classification lives in one place and needs its own test matrix (`MicrosoftFailureClassifierTests`).

**Note on missing app roles.** With client credentials, a service principal whose application permission was removed still receives a token, just without that role. The refusal then shows up as a 401/403 from the API, which is why HTTP status on floor calls is part of the `GrantRevoked` and `FloorPermissionRemoved` signals.
