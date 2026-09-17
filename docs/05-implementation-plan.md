# 05 — Implementation Plan

Phases are sequential. Tasks within a phase are ordered. Each task has acceptance criteria (AC). Claude Code should pick up one task at a time, restate the AC, propose a plan, then implement with tests.

Estimates assume one developer working with Claude Code.

---

## Phase 0 — Foundation + trust (3–4 weeks)

**Exit criteria:**
- A non-admin can sign in and request admin consent.
- An admin connects the tenant, and the consent is **verified**.
- Capability discovery runs.
- The floor sync framework executes against WireMock.
- The tenant-isolation suite is green.
- The deployment pipeline deploys both regional stacks behind Front Door, migrating before traffic moves.

| ID | Task | AC |
|---|---|---|
| P0-1 | Solution scaffold per `CLAUDE.md` layout (.NET 10, ADR-014); CI (format, build, unit, integration with Docker, migrations + pending-model check, Bicep lint, NuGet vulnerabilities); Docker compose (SQL Server, Redis, Service Bus emulator) | CI green; `docker compose up` gives local dependencies; the TenantIsolation count floor is enforced |
| P0-2 | Two Entra multi-tenant registrations (core + Usage Insights, ADR-015; `infra/entra.md`): `groupMembershipClaims: DirectoryRole`, optional `auth_time` claim, app role `SubscriptionManager`; Microsoft.Identity.Web sign-in with the Key Vault certificate; custom publisher domain; privacy/terms URLs; CPP publisher verification started for both | Sign-in works from a second test tenant; the consent screen shows the publisher domain; the ID token carries `wids` for an admin; verification submitted |
| P0-3 | Tenant isolation: `TenantId` base entity, `ITenantContext` from `tid`, EF global filter, RLS migration + connection interceptor with the `mlcp_system` system clause (ADR-026), `[TenantScoped]` → 404 | `TenantIsolation` category: cross-tenant reads through every repository and endpoint return zero rows / 404; a connection without `mlcp_system` that sets `IsSystem` sees nothing |
| P0-4 | Key Vault + separate managed identities (web, worker, migrator); certificate credential for app-only and sign-in; user-secrets locally; Serilog + redaction enricher; certificate secret excluded from `IConfiguration` | No secrets in repo or config; a logged request with a bearer token shows `[REDACTED]` |
| P0-5 | MSAL distributed token cache on Redis (Azure Managed Redis with Entra auth in Azure), encrypted, with Data Protection keys in Key Vault | Two web instances share sessions; no token in SQL; no Redis access key exists |
| P0-6 | Onboarding state machine (ADR-016, ADR-018): `NotConnected` → admin-only connect → bound single-use `state` → callback → **app-only `GET /organization` verification** (→ `Provisioning`, or `ConsentPendingVerification` with retries at +2/+5/+15 min) → discovery queued. Owner derived from `wids` at every sign-in; everyone else Viewer. "Ask my admin" email with rate limits and verified-domain recipients. The callback never cancels deletion. Static preview on the NotConnected page (ADR-025) | End-to-end: analyst signs in (Viewer), request emailed, admin connects, tenant `Active`. Negative tests: a forged or replayed callback, a callback from another user or tenant, and a non-admin `connect` are all refused; a non-admin never becomes Owner; a demoted admin loses Owner at the next sign-in |
| P0-7 | Capability discovery job: Graph probe (core token), usage probe (Usage Insights token), ARM probe (`subscriptions`, `billingProperty`), Billing probe (`billingAccounts`, `billingRoleAssignments`), partner signal; non-throwing probes; writes `TenantCapabilityProfile` with reasons, verified domains and the cost-scope rung; positive-evidence agreement classification including `Undetermined` (ADR-020) | For fixtures MOSA / MCA / EA / CSP-managed / Undetermined, the profile matches `01-scope` §4; a tier-1-only tenant reaches `Active` with `GraphUsage = Tier2NotGranted` |
| P0-8 | Sync framework: Service Bus scheduler + worker; `SyncRun` with per-run `LoadMode`, `PeriodKey`, `StagedRowCount`; staging tables; validation gate (ADR-024: staged-count baseline per mode and period, invoice-rollover exception, audited one-shot `SyncGateOverride`); MERGE and `Succeeded` in one transaction inside the execution strategy; `sp_getapplock` per (tenant, job); abandoned-run sweep; checkpoint and resume; failure classification (ADR-016) with the re-consent probe; Polly with all three retry headers, per-(tenant, provider) breaker, per-provider limiter, Retry-After budget; `ClientType` header | Fault-injection tests: 429 honours Retry-After; an empty response does not wipe the live table; a quiet run does not lower the baseline; a 403 on a floor call flags the tenant, while a 403 on usage/ARM/billing flags only that capability; transient errors keep the verdict; `invalid_client` flags no tenant; a second concurrent run is `Skipped`; a failed run resumes from its checkpoint; an override lets exactly one run through |
| P0-9 | Onboarding checklist UI (Graph ✓ / Usage reports / Azure RBAC / Billing role) with remediation links; `infra/customer-rbac.bicep` + generated `customer-rbac.json` and the Deploy to Azure link from `Mlcp:CustomerRbacTemplateUrl` | The checklist reflects the capability profile, `Undetermined` shows Guide C as actionable, and the template assigns Cost Management Reader at a management group |
| P0-10 | Regional routing (ADR-021): region picker, region confirmed and claimed at connect, `TenantRegions` directory, `RegionRoutingMiddleware`. Bicep: `global.bicep` (Front Door, directory) and `main.bicep` (Container Apps, SQL, Managed Redis, Service Bus, Key Vault, private endpoints, three identities, migrate/create-users jobs). Deploy pipeline (ADR-026): build → what-if → approval → deploy at 0% → migrate job → smoke `/health/ready` → traffic → deactivate | `az bicep lint` clean. EU and US stacks deploy. A user of an EU tenant who lands on the US host is redirected. A deployment with a failing migration never moves traffic. Private endpoints enforced. No workstation access to SQL needed |
| P0-11 | Disconnect (live admin + recent sign-in) → GracePeriod → Deleted job; audit rows purged in the same transaction; `DeletionCertificate` with per-table counts and the audit-log SHA-256 written atomically (ADR-019); DPA + privacy pages | Deleting a tenant removes all rows across all tables, including `AuditLog` (verified by test); the certificate exists only if the delete committed; `mlcp_web` cannot UPDATE or DELETE `AuditLog` (test) |

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
| P1-10 | Application RBAC: Owner (derived from `wids`), Analyst and Viewer (stored); Owners assign Analyst/Viewer; invite users within the tenant | Viewer cannot edit prices; Owner can invite and change roles; the Owner role cannot be assigned in-app |
| P1-11 | Interactive demo dashboard with sample data for the NotConnected state; replaces the Phase 0 static preview (ADR-025) | Visible before consent; clearly labelled as sample data; makes no Microsoft calls |
| P1-12 | Free-tier limits + basic billing hooks (Stripe or Marketplace SaaS — ADR-009) | Tier enforced |

---

## Phase 2 — Usage insights (2 weeks)

| ID | Task | AC |
|---|---|---|
| P2-1 | Tier-2 consent = admin consent to the **second registration, MLCP Usage Insights** (ADR-015); callback `/onboarding/usage-insights-callback` with the same state and verification rules as P0-6; explanation UI (Guide D) | Consent verified with an app-only call as Usage Insights; capability profile updates; revoking Usage Insights leaves the core connection `Active` |
| P2-2 | `GraphUsageProvider` using the Usage Insights token: `/reports/*` 302 → CSV download; Copilot report 200 with a CSV body; parse the reports in `02-api` §1.2 | All listed reports parsed; anonymised rows keep the hash in `UserKey` |
| P2-3 | `UsageReportSync` as two schedules (D7 daily, D90 weekly) → `UserActivity` | Aggregate active-vs-licensed per workload |
| P2-4 | Anonymisation opt-in flow: read the v1.0 `GET /admin/reportSettings` (`displayConcealedNames`; needs `ReportSettings.Read.All`, not yet requested by either registration, so add it to Usage Insights first or rely on the admin's attestation); feature flag `IdentifiableNames`; logged; reversible. MLCP never PATCHes the setting | Per-user and department views appear only after opt-in |
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
| P3-8 | Organisation roll-up (ADR-022): `AssociatedTenantDiscovery` weekly + connect prompt for new tenants; bilateral `OrganizationViewGrant` (request in the viewer tenant, approval by a verified admin of the target); RLS `OrgView` clause; `[OrganizationRollup]` GET endpoints; billing-tenant ownership of billing rows | A new associated tenant appears in the roll-up only after its admin approves; revoked grant → nothing visible; roll-up is read-only (a write is blocked); isolation suite extended |

---

## Phase 4 — Azure cost unlock (4–5 weeks)

| ID | Task | AC |
|---|---|---|
| P4-1 | `CostScopeResolver` (ADR-017): rung 1 billing profile / EA billing account, rung 2 root management group (EA, pay-as-you-go), rung 3 per subscription (MCA/CSP without a billing role). `CostManagementProvider`: Query (grouped server-side), Forecast, Dimensions; `ClientType` header; `nextLink` pagination; QPU budget | At most one query per (scope, grouping) per sync; rung chosen and logged per sync; rung 3 is sequential and within the QPU budget; "Purchases need billing access" panel on rungs 2–3; fixtures for each rung |
| P4-2 | `AzureCostSummarySync` 6h → `ConsumptionFact` (subscription/service grain) + `CostMonthlyRollup` | Dashboard reads rollup only |
| P4-3 | `AzureCostDetailSync` daily via `generateCostDetailsReport`, per billing profile (rung 1) or per subscription, **never per management group**, at most once a day per scope; Exports for datasets over ~2 GB/month → resource-level `ConsumptionFact`; partitioning | RG → resource → meter drill-down; "unallocated" bucket for non-RG charges |
| P4-4 | Resource-group and resource views; tag grouping; actual vs amortised toggle; "as of" labels | Matches fixture totals; untagged residual shown |
| P4-5 | Forecast card; MoM anomaly threshold (own logic) + Cost Management alerts read | Alerts surface; forecast renders when history ≥ 30 days |
| P4-6 | `ReservationSync` (recommendations, details, summaries; Consumption APIs are in maintenance mode, so prefer the Cost Management equivalents where they exist), `AdvisorSync` (Cost) | Savings opportunities panel |
| P4-7 | Blended M365 + Azure dashboard for MCA (per `03-architecture` §9); source labels | Billed months from transactions; current month blended; each labelled |
| P4-8 | Department/cost-centre dimension: `DirectoryUser.Department` ↔ `AzureSubscription.CostCenter`/tags; BU roll-up | Enterprise persona view works on fixtures |
| P4-9 | Optional scoped RBAC (subscription/invoice-section scoping for app users) | Scoped Viewer sees only assigned scopes |

---

## Phase 5 — Lifecycle operations (3–4 weeks, MCA)

**Gate:** write-tier controls in `03-architecture` §8 implemented first.

| ID | Task | AC |
|---|---|---|
| P5-1 | Write tier (ADR-010, ADR-023): delegated-only path; **Entra app role `SubscriptionManager`** from the `roles` claim, independent of Owner; tenant flag `LifecycleOps` (Owner-only); audit Attempt row before the call and Outcome row after; Guide F | App-only credentials cannot reach write endpoints (test); an Owner without the app role is refused; a SubscriptionManager without the flag is refused |
| P5-2 | Auto-renew toggle (`PATCH billingSubscriptions`) with confirmation | Change reflected after re-sync; audit row |
| P5-3 | Quantity change: increase via alias PUT / decrease via `split`; pre-flight `systemOverrides`/`cancellationAllowedEndDate`; £ impact confirmation | Refuses outside window with explanation |
| P5-4 | Cancellation of a seat-based MCA subscription (`DELETE billingSubscriptions/{id}`; `POST .../cancel` is MPA only) with window guard | Same |
| P5-5 | Licence assignment via Graph `assignLicense` (user and group), delegated **`LicenseAssignment.ReadWrite.All`**, requested incrementally | Assign/remove works; audit |

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
