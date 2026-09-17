# 03 — Architecture

## 1. System view

```
                ┌─────────────────────────────────────────────────────────────┐
                │ Microsoft Entra ID — two multi-tenant registrations (ADR-015)│
                │   MLCP (core): sign-in, floor, RBAC/billing principal        │
                │   MLCP Usage Insights: Reports.Read.All only                 │
                └──────────────┬──────────────────────────────────────────────┘
                               │ OIDC (code + PKCE)          app-only (certificate)
                               ▼
  Azure Front Door ── app.<domain> (any region) ──┬── eu.app.<domain> ──┐
  (infra/global.bicep)                            └── us.app.<domain> ──┤ one stack per region
                                                                        ▼
   ┌──────────────────────────────────────────────────────────────────────────────┐
   │ Regional stack (infra/main.bicep), VNet-integrated Container Apps environment │
   │                                                                              │
   │  Mlcp.Web  (container app, web identity)        Mlcp.Sync (container app,    │
   │   MVC + Razor + htmx │ /api/v1                   worker identity, 1 replica)  │
   │   RegionRoutingMiddleware                        scheduler + SB consumer     │
   │          │ reads                                        │ writes via SyncRun │
   │          ▼                                              ▼                    │
   │   Azure SQL (RLS) ◄───────────────────────────── staging → gate → MERGE      │
   │   Azure Managed Redis (MSAL cache, nonces)      Service Bus (sessions, DLQ)  │
   │   Key Vault (certificate, DP key)               migrate / create-users jobs  │
   │   all behind private endpoints                  (migrator identity)          │
   └──────────────────────────────────────────────────────────────────────────────┘
          │ tid → region                                     │ Polly, ClientType, breakers
          ▼                                                  ▼
  Global TenantRegions table            Microsoft Graph │ ARM: Billing / Cost Management /
  (RA-GZRS, Entra-only)                 (licences,      │ Consumption / Advisor │ (Phase 6)
                                         usage)         │ Partner Center
```

**Why one deployable.** Splitting MVC from the API would force On-Behalf-Of at every hop and add
a registration whose permissions a user cannot attribute to actions. The internal layers stay
strict, so extracting the API later is a deployment change (ADR-001).

**Why two registrations.** Admin consent with `/.default` grants every application permission
configured on a registration. Tier 2 can only be granted and revoked separately if it is a
separate registration (ADR-015).

## 2. Integration layer and capability gates

```
ILicenseProvider              → GraphLicenseProvider                                    (all tenants)
IUsageProvider                → GraphUsageProvider  | Unavailable(Tier2NotGranted | RoleRevoked | UsageAnonymised)
IMicrosoft365CommerceProvider → McaBillingCommerceProvider
                                | Unavailable(NotMca | BillingRoleMissing | CspManaged | Mosa)
IAzureCostProvider            → CostManagementProvider | Unavailable(NoAzure | RbacMissing | ClassicCsp)
IAzureBillingProvider         → AzureBillingProvider   | Unavailable(BillingRoleMissing | NoBillingAccount)
IPartnerCenterProvider        → (Phase 6)              | Unavailable(NotPartner | IndirectReseller)
```

- **Unavailable is a value.** `CapabilityUnavailable(Reason, RemediationLink, Message)` is a value
  type, and application services return `Result<T, CapabilityUnavailable>`. Views render the
  reason.
- **Common reasons.**
  - `ConsentRevoked` / `RoleRevoked` mean a grant was lost.
  - `ProviderError` marks a stale verdict kept through a transient failure (ADR-016).
  - `NotDiscovered` means discovery has not run yet.
- **Capability profile.** `TenantCapabilityProfile` stores the resolution (§5 of docs/04). It is
  recomputed:
  - on schedule;
  - after each unlock;
  - on a `GrantRevoked` / `FloorPermissionRemoved` classification;
  - by the re-consent probe.

## 3. Onboarding state machine

```
Visitor ── signs in (core app, delegated User.Read) ──► tenant row exists and connected? ── yes ──► Dashboard
                                                          │ no
                                                          ▼
                       NotConnected  (static "preview" in Phase 0; interactive demo is P1-11, ADR-025)
                         ├─ caller has admin wids (Global Admin / Privileged Role Admin)
                         │     └─ POST /onboarding/connect (region confirmed) ──► Entra admin consent
                         └─ anyone else: "Ask my admin" ──► PendingConsentRequest
                               (email: 3/user/24 h, 10/tenant/24 h, 15-min cooldown, verified domains only)
                                                          │
                GET /onboarding/consent-callback  (state: DP-protected {tid, oid, nonce, 10-min expiry};
                nonce single-use in Redis; Microsoft's tenant == caller tid; the callback proves nothing)
                                                          ▼
                verify: app-only token for tid → GET /organization
                   ├─ 200 ─────────────────────────────► Tenant.GrantConsent → Provisioning
                   ├─ AADSTS700016 / 7000229 (propagation) ─► ConsentPendingVerification
                   │        └─ ConsentVerification job at +2, +5, +15 min ─► Provisioning, or back to NotConnected
                   └─ anything else ─► "Consent could not be confirmed" (stays NotConnected)
                                                          ▼
                Provisioning ──► CapabilityDiscovery job (queued, never run in the request)
                                   ├ Graph probe (subscribedSkus, directory/subscriptions, organization)
                                   ├ Usage probe with the Usage Insights token (tier 2)
                                   ├ ARM probe (subscriptions, billingProperty per subscription)
                                   ├ Billing probe (billingAccounts → agreementType; billingRoleAssignments)
                                   └ Partner signal (ownerTenantId)
                                                          ▼
                Active (floor syncing) + checklist {Graph ✓ | Usage reports | Azure RBAC | Billing role}
                   │  each unlock ──► discovery re-run for that provider
                   │
                   │  GrantRevoked / FloorPermissionRemoved (ADR-016) — nothing else
                   ▼
                NeedsReconsent (sync halted, Owners notified)
                   ├─ ReconsentProbe (floor only) at +1 h, +6 h, +24 h, then daily for 30 days, then weekly
                   ├─ "Check again" (Owner, at most once per 5 min)
                   └─ probe succeeds ──► Active (audit: ConsentRestored)

Active / NeedsReconsent ── Disconnect (live admin wids + sign-in ≤ 15 min) ──► GracePeriod (30 days)
GracePeriod ── POST /onboarding/cancel-disconnect (same checks) ──► previous state
GracePeriod ── TenantDeletion job ──► rows deleted + DeletionCertificate (one transaction)
```

- **Persistence.** The machine is persisted as `Tenant.Status` plus `OnboardingStep` rows. Every
  transition is idempotent and resumable.
- **The callback never changes state.** A consent callback never cancels a scheduled deletion,
  and it never marks a tenant consented by itself (ADR-018).
- **Region.** The region is claimed in the global directory when the admin connects, not at first
  sign-in (§5.3).

## 4. Authentication & authorization

### 4.1 Three authorisation systems

| System | Governs | Granted where | Probe |
|---|---|---|---|
| Entra app consent (Graph application permissions) | Directory, licences (core app); usage reports (Usage Insights app) | Admin consent screen, one per registration | `GET /organization`, `GET /subscribedSkus`; the usage probe uses the Usage Insights token |
| Azure RBAC | Cost Management, Consumption, Advisor | Management group or subscription IAM → **Cost Management Reader** for the MLCP enterprise application | `GET /subscriptions`, then a scoped `query` |
| Billing roles | `Microsoft.Billing`; billing-scope Cost Management | MCA: *Cost Management + Billing → Billing scopes → Access control (IAM)*. EA: service-principal roles (EnrollmentReader) through the REST API only | `GET /billingAccounts`, `.../billingRoleAssignments` |

- Admin consent grants **nothing** on ARM. The onboarding checklist shows each system separately,
  with its own guide (`07-onboarding-guides.md`).
- `infra/customer-rbac.json` is the one-click management-group assignment. Its raw URL is
  `Mlcp:CustomerRbacTemplateUrl`.

### 4.2 Flows

| Path | Flow |
|---|---|
| User → Web | OIDC code + PKCE with the core app. Cookie session: host-only, `Secure`, `HttpOnly`, **`SameSite=Lax`** (`Strict` breaks the `form_post` return from Entra). Code redemption uses the Key Vault certificate |
| Web → Graph, onboarding | **App-only**: the consent-verification call and any probe run with client credentials for the tenant, never with the user's token |
| Web/Sync → Graph, ARM | Client credentials with the certificate. Tokens are acquired per (tenant, app, audience), and `TokenAudience.GraphReports` uses the Usage Insights app. The tenant's grants to the MLCP enterprise application authorise ARM calls |
| Lifecycle ops (Phase 5) | Delegated only: the user's own billing role, the `SubscriptionManager` app role, and the tenant feature flag |

### 4.3 Consent tiers

| Tier | Grant | Registration | Unlocks |
|---|---|---|---|
| 0 | `openid profile User.Read` (delegated) | Core | Sign-in; the "Ask my admin" flow |
| 1 | `Organization.Read.All`, `User.Read.All` (application) | Core | Universal floor |
| 2 | `Reports.Read.All` (application) | **Usage Insights** | Usage insights |
| 3 | RBAC **Cost Management Reader** for the core enterprise application | Core service principal | Azure cost (scope ladder, ADR-017) |
| 4 | Billing role on billing account or profile | Core service principal | Prices, invoices, auto-renew, billing-scope cost |
| 5 | The user's own billing role + app role `SubscriptionManager` + tenant flag `LifecycleOps` | Core (delegated) | Lifecycle ops |

- Each tier is a separate, revocable grant.
- Revoking a higher tier never breaks a lower one. A refused or revoked tier marks only its
  capability unavailable (ADR-016).
- A token-endpoint `AADSTS700016` / `AADSTS7000229` for the Usage Insights app means
  `GraphUsage = Unavailable(Tier2NotGranted)`. It never touches `Tenant.Status`.

### 4.4 Application roles (within a tenant)

| Role | Source | Can |
|---|---|---|
| **Owner** | **Derived** at every sign-in from the ID token `wids` claim: Global Administrator (`62e90394-…`) or Privileged Role Administrator (`e8611ab8-…`) (ADR-018) | Everything; connect, disconnect, manage users, feature flags. Destructive actions re-check the live claim and require a sign-in within the last 15 minutes |
| **Analyst** | `AppUser.Role`, assigned by an Owner (P1-10) | All read; manual prices |
| **Viewer** | `AppUser.Role`. The default for everyone else, including pre-consent sign-ins | Dashboards |
| **SubscriptionManager** | **Entra app role**, assigned in the customer's tenant, read from the `roles` claim (ADR-023). Independent of Owner | Phase 5 writes, together with the other write-tier gates |

- `AppUser.Role` records the derived Owner for display and audit only. A demoted admin loses
  Owner at their next sign-in.
- Optional scoping of roles to subscriptions or invoice sections is Phase 4 (P4-9).

## 5. Multi-tenancy

### 5.1 Isolation: four layers, shipped together

1. `TenantId` on every business table, part of every unique key.
2. EF global query filter from `ITenantContext`, which is resolved from the validated `tid`.
3. SQL row-level security: the security policy `dbo.TenantSecurityPolicy`, with FILTER and BLOCK
   predicates on `SESSION_CONTEXT('TenantId')`, stamped read-only on connection open by the
   `DbConnectionInterceptor`.
   - The system clause requires `IsSystem = 1` **and** membership of `mlcp_system` (or
     `db_owner`). A web connection that sets the flag still sees nothing (ADR-026).
4. API boundary: the `[TenantScoped]` filter compares the route/DTO tenant with the context, and
   a mismatch returns 404.

The `TenantIsolation` integration suite attempts cross-tenant reads through every repository and
endpoint, and every attempt must return zero rows or 404. It is required on every PR, and CI
asserts a minimum test count.

### 5.2 Enterprise multi-tenant (roll-up) — design, implemented in P3-8 (ADR-022)

- **Rule 2 is unchanged.** The acting tenant is always `tid`.
- **Grants.** Cross-tenant visibility comes only from an **`OrganizationViewGrant`** (viewer →
  target). It is created when a verified admin **of the target tenant** approves a link request
  from the viewer tenant, so approval is bilateral, and either side can revoke.
- **RLS.**
  - The FILTER predicate admits target tenants with an unrevoked grant, but only when
    `SESSION_CONTEXT('OrgView') = 1`. That flag is stamped only for `[OrganizationRollup]` GET
    endpoints.
  - The BLOCK predicate stays single-tenant, so roll-up views are read-only by construction.
- **EF and the API boundary.** EF mirrors the predicate with a request-scoped set of visible
  tenants, and `[TenantScoped]` returns 404 for any tenant outside that set.
- **Billing tenant.** Billing-account-level rows (`BillingAccount`, `BillingProfile`,
  `InvoiceSection`, `Invoice`, `TransactionFact`) belong to the **billing tenant**: the tenant
  whose service principal holds the billing role. Associated tenants see them only through a
  roll-up grant from the billing tenant.
- **Associated tenants.** Associated billing tenants (MCA) are listed by
  `AssociatedTenantDiscovery`, which prompts those tenants to connect.
- **Azure Lighthouse** is optional and ARM-plane only; it is not Graph.

### 5.3 Regions (ADR-021)

- **Stacks.** There is one complete stack per region (EU and US at launch), behind one Front Door
  host (`app.<domain>`). The regional hosts are `eu.app.<domain>` and `us.app.<domain>`.
- **Directory.** A global, geo-redundant `TenantRegions` table maps `tid → region`. It stores only
  the tenant GUID, the region code and a timestamp. Each stack's identities hold *Storage Table
  Data Contributor* on that table only.
- **Choosing a region.**
  - The landing page's region picker defaults from the Front Door geo header.
  - The Connect page states the region explicitly, and the admin confirms it as part of connect.
    The confirmation is recorded in the audit row.
  - **The region is claimed in the directory when the admin connects**, with a conditional
    insert. If another region won the race, the user is redirected there.
- **Sign-in (`RegionRoutingMiddleware`, after authentication).** The middleware looks up `tid`
  (cached for 10 minutes):
  - a different region → redirect to that region's host and drop this host's cookie (cookies are
    host-only);
  - this region, or unknown → continue.
- **Changing region** is an ops-assisted export, delete and re-register. It is never automatic.
- **Local development** uses an in-memory directory and a single region.

## 6. Sync architecture

The worker is a long-running Container App: one replica per region, with Service Bus sessions for
per-tenant ordering. Container Apps Jobs are used for migrations, and for an overflow worker only
if queue depth ever demands it (ADR-026).

### 6.1 Jobs

| Job | Cadence | Provider | Load mode | Notes |
|---|---|---|---|---|
| CapabilityDiscovery | On connect; weekly; after each unlock; after a revocation classification | All | Full | Queued, never run in a request |
| ConsentVerification | +2, +5, +15 min after a consent callback that hit propagation | Graph (core) | — | ADR-018 |
| ReconsentProbe | +1 h, +6 h, +24 h, then daily for 30 days, then weekly, while `NeedsReconsent` | Graph (core, floor only) | — | ADR-016 |
| LicenseSkuSync | Every 4 h | Graph | Full | Small |
| UserAssignmentSync | Daily | Graph | Incremental (`/users/delta`); **Full** every 30 days (per-run mode, ADR-024) | |
| UsageReportSync | **Two schedules**: D7 daily; D90 weekly | Graph (Usage Insights) | Append | Reports lag 24–48 h |
| BillingSubscriptionSync | Every 6 h | Billing | Full | MCA/EA/MPA |
| TransactionSyncUnbilled | Daily | Billing | Full within `PeriodKey` | A drop is allowed only after `InvoiceSync` recorded an invoice for the period |
| TransactionSyncBilled | Daily check; once per invoice | Billing | Append | Immutable |
| InvoiceSync | Daily | Billing | Full | Metadata only; no download URLs |
| UnitPriceDerivation | After TransactionSync | — | Full | Deterministic |
| AzureCostSummarySync | Every 6 h | Cost Management Query | Append | Scope from `CostScopeResolver` (ADR-017); grouped server-side |
| AzureCostDetailSync | Daily | Cost Details / Exports | Append | Per billing profile or per subscription, never per management group; at most once a day per scope (Microsoft's recommendation) |
| ReservationSync | Daily | Consumption / Cost Management | Full | |
| AdvisorSync | Daily | Advisor | Full | |
| SnapshotJob | Daily, **in the tenant's stagger slot** | — | Append | Immutable positions |
| AssociatedTenantDiscovery | Weekly | Billing | Full | |
| TenantDeletion | When a grace period ends | — | — | One transaction, with the certificate |

- **Enqueueing.** The scheduler enqueues `(TenantId, JobType, RunId)` to Service Bus, with
  session id = tenant and duplicate detection.
- **Stagger.** Each tenant runs at `hash(TenantId) mod 60` minutes past its schedule. This applies
  to every job, including `SnapshotJob`.
- **Deferral.** Retry-After hints above the budget re-queue the job with a scheduled enqueue time
  (§6.3).

### 6.2 Pipeline (every job, ADR-013, ADR-024)

```
1. Acquire sp_getapplock('sync:{tenant}:{job}'). If already held → SyncRun(Skipped).
   Runs left Running longer than 2 × max duration are swept to Abandoned.
2. Resume or start:
   - Adopt the latest Abandoned/Failed run of the same (tenant, job, PeriodKey) that has a
     continuation token and staging rows and is less than 24 h old (the old run → Superseded).
   - Otherwise create SyncRun(Running, LoadMode, PeriodKey).
3. Fetch → staging_{entity} tagged SyncRunId. CheckpointAsync(token) after each page.
   Failures are classified once (ADR-016, table below).
4. Validation gate:
   - required fields and sane ranges: every mode;
   - Full runs only: not empty when the baseline was non-empty; StagedRowCount ≥ 50% of the
     baseline.
   The baseline is the last Succeeded run with the same LoadMode and PeriodKey, compared on
   StagedRowCount. A PeriodKey with no successful run → first run, no volume check.
   An unexpired SyncGateOverride is consumed (audited) and passes the volume check.
   Fail → SyncRun(Failed), alert, staging cleared, live tables untouched.
5. Inside Database.CreateExecutionStrategy().ExecuteAsync:
   one transaction = MERGE staging → live on the natural key
                     + SyncRun(Succeeded, StagedRowCount, RecordsProcessed).
   Terminal states are final.
6. Clear staging for SyncRunId. Staging for resumable failures is kept and swept after 24 h.
```

**Failure handling** (classified in `Mlcp.Shared.Resilience`, ADR-016):

| Kind | Effect |
|---|---|
| GrantRevoked (core token errors after the propagation window; 401 on a floor call with a fresh token) | Tenant → `NeedsReconsent`; floor capabilities → `ConsentRevoked`; no retry |
| FloorPermissionRemoved (403 on `subscribedSkus` / `directory/subscriptions` / `organization`) | Tenant → `NeedsReconsent`; `GraphLicensing` → `ConsentRevoked`; no retry |
| CapabilityDenied (401/403 on any other call; Usage Insights token errors) | That capability only → `Tier2NotGranted` / `RbacMissing` / `BillingRoleMissing` / `RoleRevoked`; no retry |
| Transient (408, 429, 5xx, timeouts, open circuit, token endpoint 5xx) | Previous verdict kept (marked stale, `ProviderError`); retried by Polly |
| PlatformCredential (`AADSTS7000215`, `AADSTS7000222`, `AADSTS700027`, `invalid_client`, Key Vault certificate failure) | No tenant changes. All sync for that app is paused process-wide; `Critical` alert; retry after 5 min |
| NotFound / other 4xx | Probe-specific (e.g. `NoBillingAccount`) |

On any 401/403, the cached token for that (tenant, app, audience) is evicted.

### 6.3 Resilience (`Mlcp.Shared.Resilience`)

The policy pipeline, in order:

1. **Retry.** Exponential backoff with jitter, never on 401/403. It honours `Retry-After`,
   `x-ms-ratelimit-microsoft.consumption-retry-after` and
   `x-ms-ratelimit-microsoft.costmanagement-qpu-retry-after`.
2. **Circuit breaker** per (tenant, provider).
3. **Per-attempt timeout.** This is the only timeout, because `HttpClient.Timeout` is infinite.
4. **Concurrency limiter** per provider.

- **Budget.** A Retry-After above the budget (60 s on interactive paths, 5 min in workers) fails
  fast, and the job is re-queued with a scheduled enqueue time.
- **Cost Management.**
  - Every call carries `ClientType: Mlcp`.
  - Per-subscription queries (rung 3) run sequentially per tenant, under the QPU budget: 12 per
    10 s, 60 per minute, 600 per hour.
- **Graph `$batch`.** Items are throttled individually, so each failed item is retried with its
  own `Retry-After`.

## 7. Data freshness

Every cost and usage figure carries `AsOfUtc` from its SyncRun and renders as "as of N hours ago".
Typical lags:
- Azure: about 4 hours.
- Usage reports: 24–48 hours.
- CSP: about 24 hours.
- Licences: the last `LicenseSkuSync` (at most 4 hours).

## 8. Security controls

| Control | Implementation |
|---|---|
| Identities | Separate user-assigned identities for web, worker and migrator. Each workload has its own contained database user: `mlcp_web_user` (role `mlcp_web`) and `mlcp_worker_user` (`mlcp_worker` + `mlcp_system`). No DDL for workloads (ADR-026) |
| Secrets | Key Vault with managed identities. One certificate credential serves both registrations; no client secrets in any deployed environment. The certificate-backed secret is never loaded into `IConfiguration` |
| Tokens | MSAL distributed cache on **Azure Managed Redis** (Entra auth, access keys disabled), encrypted with Data Protection keys wrapped by Key Vault. No tokens in SQL |
| Transport | TLS 1.2+, HSTS, CSP header, anti-forgery on all POSTs, forwarded headers from the Container Apps ingress, and `AllowedHosts` restricted to MLCP's own host names |
| Data at rest | Azure SQL TDE. Consider Always Encrypted for manual prices and invoice totals |
| Network | Private endpoints for SQL, Redis, Service Bus, Key Vault and the Data Protection storage, all with public access disabled. Migrations and user creation run as jobs inside the VNet |
| Isolation | Four layers (§5.1). The RLS system bypass requires membership of `mlcp_system` |
| Audit | `AuditLog` is **append-only, enforced by database roles**: `mlcp_web` has SELECT/INSERT and DENY UPDATE/DELETE, and `mlcp_worker` also has DELETE, used only by tenant deletion. Actions with an outbound call write an Attempt row before it and an Outcome row after it (ADR-019) |
| Logging | Serilog with a redaction enricher (Authorization headers, tokens, secrets, SAS URLs) and correlation IDs |
| Write tier | Delegated only; the `SubscriptionManager` app role; flag `LifecycleOps`; pre-flight window checks; confirmation showing the financial impact; audit Attempt before the call |
| Owner actions | Derived from live `wids`. Disconnect and cancel-disconnect also require a sign-in within the last 15 minutes. The consent `state` is bound and single-use |
| Privacy | The usage anonymisation opt-in is explicit, logged, reversible and explained. MLCP never changes the tenant's report setting silently |
| Deletion | Self-service disconnect → 30-day grace period → one transaction that deletes every tenant row, including the audit log, and writes a **DeletionCertificate**. The certificate holds per-table row counts and a SHA-256 digest of the canonicalised audit log, with no personal data |
| Deployment | Schema leads code: migrations run before traffic moves. New web revisions are smoke-tested at 0% traffic |
| Compliance | DPA and subprocessor list before the first paying tenant; SOC 2 Type II within 12 months; Microsoft 365 App Compliance publisher attestation |

## 9. Unified cost model (MCA)

Two sources at different grains, never summed:

| Fact | Source | Grain | Use |
|---|---|---|---|
| `ConsumptionFact` | Cost Management | Daily, resource/meter, pre-tax | Trends, forecast, resource-group/resource views, optimisation |
| `TransactionFact` | Billing Transactions | Line item and service period; billed with tax | "What we paid", seat prices, invoice reconciliation, M365 spend |

- **Dashboard total.** Billed months come from `TransactionFact`. The current month is
  `ConsumptionFact` plus unbilled `TransactionFact`. Each figure is labelled with its source.
- **`DerivedUnitPrice` priority:** `BillingTransaction` > `PartnerShared` > `Manual` >
  `ListPriceReference`.
