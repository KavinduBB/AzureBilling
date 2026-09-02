# 03 — Architecture

## 1. System view

```
                     ┌──────────────────────┐
                     │   Microsoft Entra ID  │  multi-tenant app, publisher-verified
                     └───────────┬───────────┘
                                 │ OIDC (code + PKCE)
                                 ▼
   ┌───────────────────────────────────────────────────────────┐
   │  Mlcp.Web  (ASP.NET Core, single deployable)               │
   │   MVC + Razor + htmx  ──┐   ┌── /api/v1 controllers        │
   │                         └─┬─┘                               │
   │                  Mlcp.Application (use cases, gates)        │
   └───────────────────────────┬───────────────────────────────┘
                               │ reads
                               ▼
   ┌───────────────────┐   ┌──────────────┐   ┌────────────────────┐
   │ Azure SQL (RLS)    │◄──│ Mlcp.Sync     │◄──│ Azure Service Bus   │
   │ + Redis cache      │   │ (CA Jobs)     │   │ jobs + DLQ          │
   └───────────────────┘   └──────┬───────┘   └────────────────────┘
                                  │ Polly, ClientType, per-tenant breakers
             ┌────────────────────┼──────────────────────┐
             ▼                    ▼                       ▼
     Microsoft Graph      ARM: Billing / Cost Mgmt /   (Phase 6) Partner Center
     (licences, usage)    Consumption / Advisor         + Graph partner billing
                                  ▲
                          Key Vault + Managed Identity (certs, DP keys, secrets)
```

**Why one deployable:** an MVC→API split forces On-Behalf-Of at every hop, a second app registration, and a consent surface with permissions the user can't attribute to actions. Internal layering stays strict so extraction is a deployment change later (ADR-001).

## 2. Integration layer and capability gates

```
ILicenseProvider            → GraphLicenseProvider                 (all tenants)
IUsageProvider              → GraphUsageProvider | Unavailable(Reason.Tier2NotGranted)
IMicrosoft365CommerceProvider → McaBillingCommerceProvider | Unavailable(Reason.NotMca | Reason.BillingRoleMissing | Reason.CspManaged | Reason.Mosa)
IAzureCostProvider          → CostManagementProvider | Unavailable(Reason.NoAzure | Reason.RbacMissing | Reason.ClassicCsp)
IAzureBillingProvider       → AzureBillingProvider | Unavailable(Reason.BillingRoleMissing | Reason.NoBillingAccount)
IPartnerCenterProvider      → (Phase 6)
```

`CapabilityUnavailable(Reason, RemediationLink, Message)` is a value type. Application services return `Result<T, CapabilityUnavailable>`. Views render the reason. `TenantCapabilityProfile` caches the resolution and is recomputed on schedule and on `invalid_grant`.

## 3. Onboarding state machine

```
Visitor
  └─ signs in (User.Read) ──► TenantKnown? ── yes ──► Dashboard
                                   │ no
                                   ▼
                        NotConnected (demo dashboard shown)
                          ├─ [I am admin] ──► AdminConsentRedirect
                          └─ [Ask my admin] ──► PendingConsentRequest (email w/ link, resend, expiry)
                                   │
                        AdminConsentCallback(tid, admin_consent=True)
                                   ▼
                        Provisioning ──► CapabilityDiscoveryJob
                                            ├ Graph probe (subscribedSkus, directory/subscriptions, organization)
                                            ├ ARM probe (subscriptions, billingProperty per sub)
                                            ├ Billing probe (billingAccounts → agreementType; billingRoleAssignments)
                                            └ Partner signal (ownerTenantId)
                                   ▼
                        Active (floor data syncing) + Checklist {GraphConsent ✓ | AzureRbac ✗ | BillingRole ✗ | UsageReports ✗}
                                   │
                        each unlock ──► re-run discovery for that provider only
                                   │
                        invalid_grant / consent revoked ──► NeedsReconsent (sync halted, admin notified)
                                   │
                        disconnect ──► GracePeriod(30d) ──► Deleted (audit certificate)
```

Persisted as `Tenant.Status` + `OnboardingStep` rows. Every transition is idempotent and resumable.

## 4. Authentication & authorization

### 4.1 Three authorisation systems

| System | Governs | Granted where | Probe |
|---|---|---|---|
| Entra app consent (Graph scopes) | Directory, licences, usage | Admin consent screen | `GET /subscribedSkus` |
| Azure RBAC | Cost Management, Consumption, Advisor | Subscription/MG IAM → `Cost Management Reader` | `GET /subscriptions` then a scoped `query` |
| Billing roles | `Microsoft.Billing` | Cost Management + Billing → billing scope IAM → `Billing account reader` / `Billing profile reader` / `Invoice manager` | `GET /billingAccounts`, `.../billingRoleAssignments` |

Admin consent grants **nothing** on ARM. The onboarding checklist shows all three separately with their own remediation guide (`07-onboarding-guides.md`). Infra ships `infra/customer-rbac.bicep` for a one-click root-MG assignment.

### 4.2 Flows

| Path | Flow |
|---|---|
| User → Web | OIDC code + PKCE; cookie session; `SameSite=Strict` |
| Web → Graph, interactive (probes, explicit actions) | Delegated, incremental consent, MSAL cache |
| Sync → Graph | Client credentials, certificate, per-tenant token, `.default` |
| Sync → ARM | Client credentials + RBAC/billing role assigned to the SP in the customer tenant |
| Lifecycle ops (Phase 5) | Delegated only; user's own billing role; app role `SubscriptionManager` |

### 4.3 Consent tiers

| Tier | Grant | Unlocks |
|---|---|---|
| 0 | `User.Read openid profile` | Sign-in, demo |
| 1 | `Organization.Read.All`, `User.Read.All` (app) | Universal floor |
| 2 | `Reports.Read.All` (app) | Usage insights |
| 3 | RBAC `Cost Management Reader` | Azure cost |
| 4 | Billing role on billing account/profile | Prices, invoices, auto-renew |
| 5 | Delegated billing contributor + app role `SubscriptionManager` + tenant feature flag | Lifecycle ops |

Each tier is a separate, revocable grant. Revoking a higher tier never breaks a lower one.

### 4.4 Application RBAC (within a tenant)

`Owner` (all, manage users), `Analyst` (all read, manual prices), `Viewer` (dashboards), `SubscriptionManager` (Phase 5 writes). Optionally scoped to subscriptions/invoice sections for large enterprises (Phase 4).

## 5. Multi-tenancy

### 5.1 Isolation — four layers, shipped together
1. `TenantId` on every business table, part of every unique key.
2. EF global query filter from `ITenantContext` (resolved from validated `tid`).
3. SQL RLS: security policy with FILTER + BLOCK predicates on `SESSION_CONTEXT('TenantId')`, set on connection open by the `DbConnectionInterceptor`.
4. API boundary: `[TenantScoped]` filter compares route/DTO tenant to context; mismatch → 404.

Integration test suite `TenantIsolation` attempts cross-tenant reads through every repository and endpoint; must return zero rows / 404. Required on every PR.

### 5.2 Enterprise multi-tenant
- Associated billing tenants (MCA): one billing account, many Entra tenants; `includeTenantSubscriptions=true`; weekly discovery job prompts Graph consent for newly associated tenants.
- Azure Lighthouse: optional for ARM-plane only; not Graph.
- `Organization` aggregate above `Tenant` for roll-up dashboards; a user in the org's home tenant with `Owner` can view all linked tenants.

### 5.3 Regions
Deploy per region (EU, US at launch). Region chosen at connection, stored on `Tenant`, never migrated silently.

## 6. Sync architecture

### 6.1 Jobs

| Job | Cadence | Provider | Notes |
|---|---|---|---|
| CapabilityDiscovery | On connect; weekly; on unlock; on invalid_grant | All | |
| LicenseSkuSync | 4h | Graph | Small |
| UserAssignmentSync | Daily (delta) | Graph | Full every 30 days |
| UsageReportSync | Daily | Graph | D7 rolling; D90 weekly |
| BillingSubscriptionSync | 6h | Billing | MCA/EA/MPA |
| TransactionSync (unbilled) | Daily | Billing | Current period |
| TransactionSync (billed) | Daily check, once per invoice | Billing | Immutable |
| InvoiceSync | Daily | Billing | Metadata only |
| UnitPriceDerivation | After TransactionSync | — | Deterministic |
| AzureCostSummarySync | 6h | Cost Mgmt Query | Billing/MG scope, grouped |
| AzureCostDetailSync | Daily | Cost Details / Exports | Resource-level |
| ReservationSync | Daily | Consumption | |
| AdvisorSync | Daily | Advisor | |
| SnapshotJob | Daily 00:15 UTC | — | Immutable positions |
| AssociatedTenantDiscovery | Weekly | Billing | |

Scheduler enqueues `(TenantId, JobType, RunId)` to Service Bus; workers compete; per-tenant sessions ensure ordering within a tenant; stagger by `hash(TenantId) mod 60` minutes.

### 6.2 Pipeline (every job)
```
1. Create SyncRun(Running)
2. Fetch → staging_{entity} tagged SyncRunId (persist continuation tokens)
   429 → Retry-After / consumption header; 5xx → backoff; 401/403 → NeedsReconsent, abort
3. Validate: not empty when previous non-empty; row count ≥ 50% of last success; required fields; sane ranges
   fail → SyncRun(Failed), alert, live untouched
4. Transaction: MERGE staging → live on natural key
5. SyncRun(Succeeded, RecordsProcessed)
6. Truncate staging for SyncRunId
```

### 6.3 Resilience (`Mlcp.Shared.Resilience`)
Retry (exp backoff + jitter, honours `Retry-After` and `x-ms-ratelimit-microsoft.consumption-retry-after`) → circuit breaker per (tenant, provider) → timeout → bulkhead per provider. Cost Management calls carry `ClientType: Mlcp`.

## 7. Data freshness
Every cost/usage figure carries `AsOfUtc` from the SyncRun and renders "as of N hours ago". Azure ~4h; usage reports ~24–48h; CSP ~24h; licences near-real-time.

## 8. Security controls

| Control | Implementation |
|---|---|
| Secrets | Key Vault, Managed Identity, certificate credential, rotation |
| Tokens | MSAL Redis cache, DP keys in Key Vault; nothing in tables |
| Transport | TLS 1.2+, HSTS preload, CSP header, anti-forgery on all POSTs |
| Data at rest | Azure SQL TDE; consider Always Encrypted on manual price / invoice totals |
| Network | Private Endpoint to SQL/Redis/Service Bus; egress allow-list to Microsoft endpoints |
| Audit | Append-only `AuditLog`; DB principal INSERT/SELECT only; every financial read and every write |
| Logging | Serilog with redaction enricher (Authorization, tokens, SAS URLs); correlation IDs |
| Write tier | Delegated only; `SubscriptionManager`; feature flag; pre-flight; confirmation with £ impact; audit-before-call |
| Privacy | Usage anonymisation opt-in is explicit, logged, reversible, with explanation |
| Deletion | Self-service disconnect → 30-day grace → hard delete → certificate |
| Compliance | DPA + subprocessor list before first paying tenant; SOC 2 Type II within 12 months; M365 App Compliance Program publisher attestation |

## 9. Unified cost model (MCA)

Two sources at different grains; never summed:

| Fact | Source | Grain | Use |
|---|---|---|---|
| `ConsumptionFact` | Cost Management | Daily, resource/meter, pre-tax | Trends, forecast, RG/resource views, optimisation |
| `TransactionFact` | Billing Transactions | Line item, service period, billed w/ tax | "What we paid", seat prices, invoice reconciliation, M365 spend |

Dashboard total: billed months from `TransactionFact`; current month from `ConsumptionFact` + unbilled `TransactionFact`; each labelled with source. `DerivedUnitPrice` priority: `BillingTransaction` > `PartnerShared` > `Manual` > `ListPriceReference`.
