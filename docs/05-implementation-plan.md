# 05 — Implementation Plan

Phases are sequential. Tasks within a phase are ordered. Each task has acceptance criteria (AC). Claude Code should pick up one task at a time, restate the AC, propose a plan, then implement with tests.

Estimates assume one developer working with Claude Code.

---

## Phase 0 — Foundation + trust (3–4 weeks)

**Exit criteria:** a non-admin can sign in, request admin consent, admin connects tenant, capability discovery runs, floor sync framework executes against WireMock, tenant-isolation suite green, deployable to one Azure region.

| ID | Task | AC |
|---|---|---|
| P0-1 | Solution scaffold per `CLAUDE.md` layout; CI (build, test, format); Docker compose (SQL Server, Redis, Azurite) | `dotnet build` and `dotnet test` green in CI; `docker compose up` gives local deps |
| P0-2 | Entra multi-tenant app registration (`infra/entra.md` runbook); Microsoft.Identity.Web sign-in; custom publisher domain; privacy/terms URLs; start CPP publisher verification | Sign-in works from a second test tenant; consent screen shows publisher domain; verification submitted |
| P0-3 | Tenant isolation: `TenantId` base entity, `ITenantContext` from `tid`, EF global filter, RLS migration + connection interceptor, `[TenantScoped]` → 404 | `TenantIsolation` test category: cross-tenant reads through every repository and endpoint return zero rows / 404 |
| P0-4 | Key Vault + Managed Identity wiring; certificate credential for app-only; user-secrets locally; Serilog + redaction enricher | No secrets in repo/config; log output of a request with a bearer token shows `[REDACTED]` |
| P0-5 | MSAL distributed token cache on Redis with Data Protection keys in Key Vault | Two Web instances share sessions; no token in SQL |
| P0-6 | Onboarding state machine: `Tenant`, `OnboardingStep`, `PendingConsentRequest`; non-admin sign-in → NotConnected view → [I am admin] / [Ask my admin] (email) → admin consent callback → Provisioning | End-to-end test: analyst signs in, request emailed, admin link completes, tenant Active |
| P0-7 | Capability discovery job: Graph probe, ARM probe (`subscriptions`, `billingProperty`), Billing probe (`billingAccounts`, `billingRoleAssignments`), partner signal; writes `TenantCapabilityProfile` with reasons | For fixtures MOSA / MCA / EA / CSP-managed, profile matches expected table in `01-scope` §4 |
| P0-8 | Sync framework: Service Bus scheduler + worker, `SyncRun`, staging tables, validation gate (empty / >50% drop / required fields), MERGE, continuation tokens, Polly policy set with both retry headers, `ClientType` header, per-(tenant,provider) breaker, 401/403 → NeedsReconsent | Fault-injection tests: 429 honours Retry-After; empty response does not wipe live table; 403 halts and flags tenant |
| P0-9 | Onboarding checklist UI (Graph ✓ / Azure RBAC / Billing role / Usage reports) with remediation links; `infra/customer-rbac.bicep` one-click template | Checklist reflects capability profile; template assigns Cost Management Reader at root MG |
| P0-10 | Region selection at connect; Bicep for App Service/Container Apps, SQL, Redis, Service Bus, Key Vault, Private Endpoints; deploy to EU | `infra/main.bicep` deploys clean; app reachable; Private Endpoints enforced |
| P0-11 | Disconnect → GracePeriod → Deleted job; DPA + privacy policy pages | Deleting a tenant removes all rows across all tables (verified by test) and writes audit certificate |

---

## Phase 1 — Universal floor + public launch (3–4 weeks)

**Exit criteria:** any tenant can connect and see licences, assignments, renewals, waste, and £ value from manual/list prices. Free tier live.

| ID | Task | AC |
|---|---|---|
| P1-1 | `GraphLicenseProvider`: `subscribedSkus`, `directory/subscriptions`, `organization`; WireMock fixtures | Provider returns typed models; fixtures cover MOSA/MCA/CSP-managed |
| P1-2 | `LicenseSkuSync`, `SubscriptionSync` (Graph source) via pipeline | Live tables populated; `Subscription.AutoRenewEnabled` NULL; `OwnerTenantId` captured |
| P1-3 | `UserAssignmentSync` with `/users/delta` + `GraphDeltaState`; full resync every 30 days | Delta applies adds/removes; 100k-user fixture completes within budget |
| P1-4 | `Product`/`ServicePlan` reference seed from Microsoft SKU mapping; `SkuOverlapRule` seed | Unknown SKU renders as part number, not error |
| P1-5 | `ListPriceReference` seed (major SKUs, GBP/USD/EUR) + ops runbook for updates | Every seeded SKU has a list price per currency |
| P1-6 | Manual price entry + CSV import (EA price sheet) → `DerivedUnitPrice(Source=Manual)`; provenance badges | Analyst can override; badge shows source; audit row written |
| P1-7 | Dashboard: seat position per SKU (purchased/assigned/available), renewal calendar (`nextLifecycleDateTime`), trial flags, per-scenario explanatory panels | Renders for all four scenario fixtures with correct unavailable-reason panels |
| P1-8 | Waste detection: unassigned seats, seats on disabled accounts, overlapping SKUs; £ value from `DerivedUnitPrice` priority | Numbers reconcile to fixtures; £ uses highest-priority price with badge |
| P1-9 | Daily `SnapshotJob` + trend charts (Chart.js) | Snapshots written; 30-day trend renders |
| P1-10 | Application RBAC (Owner/Analyst/Viewer); invite users within tenant | Viewer cannot edit prices; Owner can invite |
| P1-11 | Demo dashboard with sample data for NotConnected state | Visible before consent |
| P1-12 | Free-tier limits + basic billing hooks (Stripe or Marketplace SaaS — ADR-009) | Tier enforced |

---

## Phase 2 — Usage insights (2 weeks)

| ID | Task | AC |
|---|---|---|
| P2-1 | Tier-2 incremental consent flow for `Reports.Read.All`; explanation UI | Consent grant recorded; capability profile updates |
| P2-2 | `GraphUsageProvider`: 302 → CSV download; parse reports in `02-api` §1.2 | All listed reports parsed; anonymised rows keep hash in `UserKey` |
| P2-3 | `UsageReportSync` D7 daily, D90 weekly → `UserActivity` | Aggregate active-vs-licensed per workload |
| P2-4 | Anonymisation opt-in flow (`/beta/admin/reportSettings`), feature flag `IdentifiableNames`, logged, reversible | Per-user/department views appear only after opt-in |
| P2-5 | Assigned-but-inactive detection (90 days) with usage-weighted £ waste; Copilot adoption card | Dashboard shows inactive assignees with £ |

---

## Phase 3 — MCA billing unlock (4–5 weeks)

**Gate:** ADR-007 spike passed (MCA-online visible via `Microsoft.Billing`).

| ID | Task | AC |
|---|---|---|
| P3-1 | `AzureBillingProvider`: billing accounts/profiles/sections/associated tenants/role assignments | Fixtures for MCA and EA |
| P3-2 | `BillingSubscriptionSync` → `Subscription(SourceSystem=AzureBilling)`; reconcile with Graph rows on `SkuId` + tenant; set `AutoRenewEnabled`, term, `CancellationAllowedEndDate` | One row per commercial subscription; no double-count |
| P3-3 | `TransactionSync` billed (once per invoice) + unbilled (daily) → `TransactionFact` | Unbilled rows replaced when billed; tax present only on billed |
| P3-4 | `UnitPriceDerivation` → `DerivedUnitPrice(Source=BillingTransaction)`; overrides manual | Price keyed by service period; badge updates |
| P3-5 | `InvoiceSync` + on-demand PDF download (202 → poll → SAS, streamed, not stored) | Download works; nothing persisted |
| P3-6 | Billing-role onboarding guide + probe explanations from `BillingRoleSnapshot` | Checklist says which role is missing at which scope |
| P3-7 | Blended M365 dashboard from `TransactionFact`; invoice list; renewal calendar with auto-renew | Totals match invoice totals for billed months |
| P3-8 | `AssociatedTenantDiscovery` weekly + Graph consent prompt for new tenants | New associated tenant appears in org roll-up after consent |

---

## Phase 4 — Azure cost unlock (4–5 weeks)

| ID | Task | AC |
|---|---|---|
| P4-1 | `CostManagementProvider`: Query (billing/MG scope, grouped), Forecast, Dimensions; `ClientType` header; pagination | Never more than 1 query per (scope, grouping) per sync; fixtures |
| P4-2 | `AzureCostSummarySync` 6h → `ConsumptionFact` (subscription/service grain) + `CostMonthlyRollup` | Dashboard reads rollup only |
| P4-3 | `AzureCostDetailSync` daily via `generateCostDetailsReport` (per subscription scope, ≤1/day) → resource-level `ConsumptionFact`; partitioning | RG → resource → meter drill-down; "unallocated" bucket for non-RG charges |
| P4-4 | Resource-group and resource views; tag grouping; actual vs amortised toggle; "as of" labels | Matches fixture totals; untagged residual shown |
| P4-5 | Forecast card; MoM anomaly threshold (own logic) + Cost Management alerts read | Alerts surface; forecast renders when history ≥ 30 days |
| P4-6 | `ReservationSync` (recommendations, details, summaries), `AdvisorSync` (Cost) | Savings opportunities panel |
| P4-7 | Blended M365 + Azure dashboard for MCA (per `03-architecture` §9); source labels | Billed months from transactions; current month blended; each labelled |
| P4-8 | Department/cost-centre dimension: `DirectoryUser.Department` ↔ `AzureSubscription.CostCenter`/tags; BU roll-up | Enterprise persona view works on fixtures |
| P4-9 | Optional scoped RBAC (subscription/invoice-section scoping for app users) | Scoped Viewer sees only assigned scopes |

---

## Phase 5 — Lifecycle operations (3–4 weeks, MCA)

**Gate:** write-tier controls in `03-architecture` §8 implemented first.

| ID | Task | AC |
|---|---|---|
| P5-1 | Write tier: delegated-only path, app role `SubscriptionManager`, tenant feature flag `LifecycleOps`, audit-before-call | Read-tier credential cannot reach write endpoints (test) |
| P5-2 | Auto-renew toggle (`PATCH billingSubscriptions`) with confirmation | Change reflected after re-sync; audit row |
| P5-3 | Quantity change: increase via alias PUT / decrease via `split`; pre-flight `systemOverrides`/`cancellationAllowedEndDate`; £ impact confirmation | Refuses outside window with explanation |
| P5-4 | Cancellation (`DELETE`) with window guard | Same |
| P5-5 | Licence assignment via Graph `assignLicense` (tier `User.ReadWrite.All`, optional consent) | Assign/remove works; audit |

---

## Phase 6 — Partner module + partner bridge (5–6 weeks, optional)

| ID | Task | AC |
|---|---|---|
| P6-1 | Partner onboarding (MPA), app-only Partner Center auth, MFA-aware; indirect reseller detection → explained refusal | |
| P6-2 | Customer enumeration, GDAP expiry monitoring | Alert before expiry |
| P6-3 | Partner Center subscription sync (`autoRenewEnabled`, `commitmentEndDate`, NCE rules) | |
| P6-4 | Graph partner billing exports (billed/unbilled usage + reconciliation) | |
| P6-5 | Partner bridge: customer invites partner; partner opts in per customer; `DerivedUnitPrice(Source=PartnerShared)` | CSP customer sees prices after partner opt-in |
| P6-6 | Partner lifecycle ops with 7-day-window guards | |

---

## Cross-cutting backlog (any phase)

- Compliance: SOC 2 controls mapping; M365 App Compliance publisher attestation
- Observability: OpenTelemetry traces across Web/Sync; per-tenant sync health page
- Performance: columnstore on `ConsumptionFact` at threshold; rollup refresh strategy
- Localisation: currency/date formatting per tenant region
- Accessibility: WCAG 2.1 AA on dashboards
