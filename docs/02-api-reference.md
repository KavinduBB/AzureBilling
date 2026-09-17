# 02 — Microsoft API Reference (Authoritative for Implementation)

Legend: ✅ supported · ⚠️ conditional · ❌ unavailable. Scenario letters per `01-scope-and-scenarios.md` §4.

**Rule:** do not use an endpoint, permission, header or field that is not listed here. To add one, add its Microsoft Learn source link first (CLAUDE.md rule 15). Every row below has a source link; all links were checked on 2026-09-17. Rows marked **Q&A** come from Microsoft Q&A answers, not product documentation, and are treated as unofficial.

Source key used in the tables: `[src]` links to the Microsoft Learn page that documents the row.

---

## 1. Microsoft Graph (v1.0 unless noted) — `https://graph.microsoft.com`

Registrations (ADR-015): **core** (MLCP) holds `Organization.Read.All` and `User.Read.All`. **Usage Insights** holds `Reports.Read.All`. All app-only calls use a certificate client credential per (tenant, app).

### 1.1 Licensing & directory

| Purpose | Request | Least-privileged permission (documented) | MLCP uses | App / Delegated | Notes | Source |
|---|---|---|---|---|---|---|
| SKU seat position | `GET /subscribedSkus` | `LicenseAssignment.Read.All` | `Organization.Read.All` (listed as higher-privileged; needed anyway for the two rows below, so no extra permission is added — ADR-015) | Both | Only `$select` is supported; **no `$filter`**. Fields: `skuId`, `skuPartNumber`, `appliesTo`, `capabilityStatus`, `consumedUnits`, `prepaidUnits{enabled,suspended,warning,lockedOut}`, `servicePlans[]`, `subscriptionIds[]`. **Floor call** | [src](https://learn.microsoft.com/en-us/graph/api/subscribedsku-list?view=graph-rest-1.0) |
| Subscription inventory | `GET /directory/subscriptions` | `Organization.Read.All` | same | Both | `companySubscription`: `id`, `commerceSubscriptionId`, `createdDateTime`, `isTrial`, `nextLifecycleDateTime`, `skuId`, `skuPartNumber`, `status` (Enabled/Deleted/Suspended/Warning/LockedOut), `totalLicenses`, `ownerId`, `ownerTenantId`, `ownerType`, `serviceStatus[]`. **No price, no auto-renew, no term.** `ownerTenantId` set to a partner tenant ⇒ scenario D. **Floor call** | [src](https://learn.microsoft.com/en-us/graph/api/directory-list-subscriptions?view=graph-rest-1.0), [resource](https://learn.microsoft.com/en-us/graph/api/resources/companysubscription?view=graph-rest-1.0) |
| Organization, verified domains, consent verification | `GET /organization?$select=id,displayName,verifiedDomains` | `Organization.Read.All` (app) | same | Both | The app-only call that **verifies admin consent** (ADR-018) and captures `verifiedDomains` (restricts consent-request recipients). **Floor call** | [src](https://learn.microsoft.com/en-us/graph/api/organization-list?view=graph-rest-1.0), [resource](https://learn.microsoft.com/en-us/graph/api/resources/organization?view=graph-rest-1.0) |
| Users + licence assignments (bulk, app-only) | `GET /users?$select=id,userPrincipalName,displayName,accountEnabled,userType,department,companyName,officeLocation,usageLocation,assignedLicenses,licenseAssignmentStates&$top=999` | `User.Read.All` | same | Both | `assignedLicenses` and `licenseAssignmentStates` are returned only with `$select`. Page via `@odata.nextLink` (max page 999). `licenseAssignmentStates` distinguishes direct and group-inherited assignment | [src](https://learn.microsoft.com/en-us/graph/api/user-list?view=graph-rest-1.0), [resource](https://learn.microsoft.com/en-us/graph/api/resources/user?view=graph-rest-1.0) |
| Incremental users | `GET /users/delta` with the same `$select` | `User.Read.All` | same | Both | **Changes to `licenseAssignmentStates` are not tracked by delta**, so the 30-day full resync (ADR-024) is what refreshes it | [src](https://learn.microsoft.com/en-us/graph/api/user-delta?view=graph-rest-1.0) |
| Per-user licence detail | `GET /users/{id}/licenseDetails` | Delegated `LicenseAssignment.Read.All`; **application: not supported** | not used by sync | Delegated only | App-only sync must use `assignedLicenses` + `servicePlans` from `subscribedSkus` instead | [src](https://learn.microsoft.com/en-us/graph/api/user-list-licensedetails?view=graph-rest-1.0) |
| Assign / remove licence (user) | `POST /users/{id}/assignLicense` | `LicenseAssignment.ReadWrite.All` | same, **delegated**, requested incrementally (Phase 5) | Both | Returns `200` with the user. Assigns existing seats only. Delegated caller also needs License Administrator / User Administrator / Directory Writers | [src](https://learn.microsoft.com/en-us/graph/api/user-assignlicense?view=graph-rest-1.0) |
| Group-based licensing | `POST /groups/{id}/assignLicense` | `LicenseAssignment.ReadWrite.All` | same, delegated (Phase 5) | Both | Returns **`202 Accepted`**. Requires Entra ID P1 | [src](https://learn.microsoft.com/en-us/graph/api/group-assignlicense?view=graph-rest-1.0) |

Consent-screen names (for Guide A copy): `Organization.Read.All` = "Read organization information", `User.Read.All` = "Read all users' full profiles", `Reports.Read.All` = "Read all usage reports", `LicenseAssignment.Read.All` = "Read all license assignments." — all admin-consent only. [src](https://learn.microsoft.com/en-us/graph/permissions-reference)

### 1.2 Usage reports (tier 2 — Usage Insights registration)

All require `Reports.Read.All` (app or delegated). Delegated callers additionally need a report-capable Entra role (e.g. Reports Reader, Global Reader — the latter and Usage Summary Reports Reader see tenant-level data only) ([authorization](https://learn.microsoft.com/en-us/graph/reportroot-authorization)). Global service only. Data lags ~24–48 h.

| Report | Request | Periods | Response | Source |
|---|---|---|---|---|
| M365 active users | `GET /reports/getOffice365ActiveUserDetail(period='D90')` | D7, D30, D90, D180 (or `date=`) | **302** → pre-authenticated CSV URL in `Location`, valid a few minutes, no `Authorization` header | [src](https://learn.microsoft.com/en-us/graph/api/reportroot-getoffice365activeuserdetail?view=graph-rest-1.0) |
| Teams | `GET /reports/getTeamsUserActivityUserDetail(period='D90')` | D7, D30, D90, D180 | 302 → CSV | [src](https://learn.microsoft.com/en-us/graph/api/reportroot-getteamsuseractivityuserdetail?view=graph-rest-1.0) |
| Exchange | `GET /reports/getEmailActivityUserDetail(period='D90')` | D7, D30, D90, D180 | 302 → CSV | [src](https://learn.microsoft.com/en-us/graph/api/reportroot-getemailactivityuserdetail?view=graph-rest-1.0) |
| SharePoint | `GET /reports/getSharePointActivityUserDetail(period='D90')` | D7, D30, D90, D180 | 302 → CSV | [src](https://learn.microsoft.com/en-us/graph/api/reportroot-getsharepointactivityuserdetail?view=graph-rest-1.0) |
| OneDrive | `GET /reports/getOneDriveActivityUserDetail(period='D90')` | D7, D30, D90, D180 | 302 → CSV | [src](https://learn.microsoft.com/en-us/graph/api/reportroot-getonedriveactivityuserdetail?view=graph-rest-1.0) |
| M365 Apps | `GET /reports/getM365AppUserDetail(period='D90')` | D7, D30, D90, D180 | 302 → CSV (JSON with `$format=application/json`) | [src](https://learn.microsoft.com/en-us/graph/api/reportroot-getm365appuserdetail?view=graph-rest-1.0) |
| Copilot | `GET /copilot/reports/getMicrosoft365CopilotUsageUserDetail(period='D7')` (v1.0) | v1: D7, D30, D90, D180, ALL | **200** with the CSV **in the body** (`application/octet-stream`), not a redirect. Only users with a Copilot licence | [src](https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/api/admin-settings/reports/copilotreportroot-getmicrosoft365copilotusageuserdetail) |
| Activations | `GET /reports/getOffice365ActivationsUserDetail` | none (no period parameter) | 302 → CSV | [src](https://learn.microsoft.com/en-us/graph/api/reportroot-getoffice365activationsuserdetail?view=graph-rest-1.0) |
| Anonymisation setting (read) | `GET /admin/reportSettings` (**v1.0**) → `displayConcealedNames` | — | 200. Least privilege `ReportSettings.Read.All` (app or delegated) — **not currently requested by either registration** (see P2-4) | [src](https://learn.microsoft.com/en-us/graph/api/adminreportsettings-get?view=graph-rest-1.0) |
| Anonymisation setting (write) | `PATCH /admin/reportSettings` | — | 204. `ReportSettings.ReadWrite.All`. **MLCP never calls this**; the admin changes the setting (Guide D) | [src](https://learn.microsoft.com/en-us/graph/api/adminreportsettings-update?view=graph-rest-1.0) |

**Anonymisation:** by default all reports conceal user, group and site names; the admin-centre setting "Conceal user, group, and site names in all reports" also governs the Graph reports ([src](https://learn.microsoft.com/en-us/microsoft-365/admin/activity-reports/activity-reports?view=o365-worldwide)). When on, user identifiers are hashed and cannot be joined to directory users; aggregate metrics still work; per-user/department views require the admin's change plus feature flag `UsageInsights.IdentifiableNames`.

### 1.3 Graph throttling and batching

| Item | Value | Source |
|---|---|---|
| Throttling | Per-app, per-tenant limits; `429` with `Retry-After` | [src](https://learn.microsoft.com/en-us/graph/throttling) |
| `$batch` | `POST /$batch`, max **20** requests. **Requests inside a batch are throttled individually**: the batch returns 200 while items return 429 with their own `Retry-After`; SDKs do not retry batch items automatically, so MLCP retries each failed item | [throttling](https://learn.microsoft.com/en-us/graph/throttling), [batching](https://learn.microsoft.com/en-us/graph/json-batching) |
| Design | Use `$select`, delta and batching; stagger tenant sync by `hash(TenantId)` | — |

---

## 2. Azure Resource Manager — `https://management.azure.com`

Authorisation is **Azure RBAC** (subscriptions/management groups) and **billing roles** (billing scopes) granted to the **MLCP enterprise application (service principal)** in the customer's tenant — not Graph permissions. Admin consent grants nothing here. See `03-architecture.md` §4.

### 2.1 Subscriptions & classification

| Purpose | Request | Role | Notes | Source |
|---|---|---|---|---|
| List Azure subscriptions | `GET /subscriptions?api-version=2022-12-01` | Reader (any role on the subscription) | | [src](https://learn.microsoft.com/en-us/rest/api/resources/subscriptions/list?view=rest-resources-2022-12-01) |
| Agreement type per subscription | `GET /subscriptions/{id}/providers/Microsoft.Billing/billingProperty/default?api-version=2024-04-01` | Reader (Cost Management Reader includes `Microsoft.Billing/billingProperty/read`) | `billingAccountAgreementType` (`MicrosoftCustomerAgreement` \| `EnterpriseAgreement` \| `MicrosoftOnlineServicesProgram` \| `MicrosoftPartnerAgreement` \| `Other`), `billingTenantId`, `billingProfileId`, `invoiceSectionId`, `costCenter`, `subscriptionBillingType`; `includeTransitionStatus=true` adds `isTransitionedBillingAccount` (MOSP → MCA). ADR-020 evidence | [src](https://learn.microsoft.com/en-us/rest/api/billing/billing-property/get?view=rest-billing-2024-04-01) |
| Management groups | `GET /providers/Microsoft.Management/managementGroups?api-version=2020-05-01` | Management group reader | REST reference is published for 2020-05-01 (2023-04-01 is the newest GA in the [template reference](https://learn.microsoft.com/en-us/azure/templates/microsoft.management/managementgroups)). The root group ID equals the tenant ID ([overview](https://learn.microsoft.com/en-us/azure/governance/management-groups/overview)) | [src](https://learn.microsoft.com/en-us/rest/api/managementgroups/management-groups/list?view=rest-managementgroups-2020-05-01) |

### 2.2 `Microsoft.Billing` — api-version `2024-04-01`

Base: `/providers/Microsoft.Billing/billingAccounts/{billingAccountName}`. List operations accept `filter`, `orderBy`, `top` (**max 50**), `skip`, `count`, `search`. Money is `{currency, value}`. Operation index: [billing REST](https://learn.microsoft.com/en-us/rest/api/billing/?view=rest-billing-2024-04-01).

| Purpose | Request | Billing role | Scenario | Notes | Source |
|---|---|---|---|---|---|
| Billing accounts | `GET /providers/Microsoft.Billing/billingAccounts` | Any billing role | B,C,E | `agreementType`: `MicrosoftCustomerAgreement` \| `EnterpriseAgreement` \| `MicrosoftPartnerAgreement` \| `MicrosoftOnlineServicesProgram` \| `Other`; `accountType`; `hasReadAccess` | [src](https://learn.microsoft.com/en-us/rest/api/billing/billing-accounts/list?view=rest-billing-2024-04-01) |
| Billing profiles | `GET .../billingProfiles` | Profile reader | B,E | MCA/MPA | [src](https://learn.microsoft.com/en-us/rest/api/billing/billing-profiles/list-by-billing-account?view=rest-billing-2024-04-01) |
| Invoice sections | `GET .../billingProfiles/{p}/invoiceSections` | Profile reader | B | MCA | [src](https://learn.microsoft.com/en-us/rest/api/billing/invoice-sections/list-by-billing-profile?view=rest-billing-2024-04-01) |
| Associated tenants | `GET .../associatedTenants` | Account reader | B | `billingManagementState`, `provisioningManagementState`; `includeRevoked` | [src](https://learn.microsoft.com/en-us/rest/api/billing/associated-tenants/list-by-billing-account?view=rest-billing-2024-04-01) |
| **Billing subscriptions** | `GET .../billingSubscriptions?includeDeleted=false` | Account/profile reader | B,C,E | Fields: `autoRenew` (On/Off), `billingFrequency`, `productCategory` (open string; docs list AzureSupport/Hardware/ReservationOrder/SaaS/SavingsPlanOrder/Software/UsageBased/Other, and samples return `SeatBased`), `productTypeId`, `skuId`, `quantity`, `termDuration`, `termStartDate`, `termEndDate`, `renewalTermDetails`, `systemOverrides{cancellation (Allowed/NotAllowed), cancellationAllowedEndDate}`, `provisioningTenantId`, `beneficiaryTenantId`, `operationStatus`, `status`. `expand=LastMonthCharges,MonthToDateCharges`. `includeTenantSubscriptions` **applies only to MOSP billing accounts** | [src](https://learn.microsoft.com/en-us/rest/api/billing/billing-subscriptions/list-by-billing-account?view=rest-billing-2024-04-01) |
| Tenant IDs on billing subscriptions | header `x-ms-service-tenant-info: true` on the list above | — | B | Optional header that returns tenant IDs associated with the billing account; documented only in the FAQ, not the REST spec | [src](https://learn.microsoft.com/en-us/azure/cost-management-billing/manage/discover-cloud-footprint) |
| By profile / section / customer / enrolment | `.../billingProfiles/{p}/billingSubscriptions` (MCA/MPA), `.../invoiceSections/{s}/billingSubscriptions` (MCA), `.../customers/{c}/billingSubscriptions` (MPA), `.../enrollmentAccounts/{e}/billingSubscriptions` (EA) | Scope reader | | | [src](https://learn.microsoft.com/en-us/rest/api/billing/billing-subscriptions?view=rest-billing-2024-04-01) |
| Update auto-renew | `PATCH .../billingSubscriptions/{id}` body `{"properties":{"autoRenew":"Off"}}` | Profile contributor | B | Phase 5, delegated only. 200 or 202 + `Location`/`Retry-After`. `operationStatus = LockedForUpdate` blocks writes | [src](https://learn.microsoft.com/en-us/rest/api/billing/billing-subscriptions/update?view=rest-billing-2024-04-01) |
| Create seat-based subscription | `PUT .../billingSubscriptionAliases/{guid}` body `{"properties":{"skuId","quantity","termDuration","billingFrequency","displayName","billingProfileId","invoiceSectionId"}}` | Profile owner/contributor | B | Phase 5. **Purchase.** Seat-based only; async | [src](https://learn.microsoft.com/en-us/rest/api/billing/billing-subscriptions-aliases?view=rest-billing-2024-04-01) |
| Reduce quantity | `POST .../billingSubscriptions/{id}/split` | Profile contributor | B | Phase 5; subject to `systemOverrides` | [src](https://learn.microsoft.com/en-us/rest/api/billing/billing-subscriptions?view=rest-billing-2024-04-01) |
| Merge | `POST .../billingSubscriptions/{id}/merge` | Profile contributor | B | Phase 5 | [src](https://learn.microsoft.com/en-us/rest/api/billing/billing-subscriptions?view=rest-billing-2024-04-01) |
| **Cancel seat-based (MCA)** | `DELETE .../billingSubscriptions/{id}` | Profile owner | B,E | Phase 5. MCA or MPA. Only within `cancellationAllowedEndDate` | [src](https://learn.microsoft.com/en-us/rest/api/billing/billing-subscriptions?view=rest-billing-2024-04-01) |
| Cancel usage-based | `POST .../billingSubscriptions/{id}/cancel` | Admin agent | E | **MPA only** | [src](https://learn.microsoft.com/en-us/rest/api/billing/billing-subscriptions?view=rest-billing-2024-04-01) |
| Move invoice section | `POST .../billingSubscriptions/{id}/move`, `.../validateMoveEligibility` | Profile contributor | B | Same billing profile only | [src](https://learn.microsoft.com/en-us/rest/api/billing/billing-subscriptions/validate-move-eligibility?view=rest-billing-2024-04-01) |
| Products (reservations, software) | `GET .../products` | Reader | B,E | MCA/MPA; excludes usage-based. `autoRenew`, `lastCharge`, `lastChargeDate`, `status`, `endDate` | [src](https://learn.microsoft.com/en-us/rest/api/billing/products/list-by-billing-account?view=rest-billing-2024-04-01) |
| **Invoices list** | `GET .../invoices?periodStartDate=&periodEndDate=` | Reader / invoice manager | B,C,E | All agreement types. Period params are **optional**; typed `date` (the description says MM-DD-YYYY but samples use `YYYY-MM-DD` — use ISO and cover with a fixture). Also `.../billingProfiles/{p}/invoices` (MCA/MPA) | [account](https://learn.microsoft.com/en-us/rest/api/billing/invoices/list-by-billing-account?view=rest-billing-2024-04-01), [profile](https://learn.microsoft.com/en-us/rest/api/billing/invoices/list-by-billing-profile?view=rest-billing-2024-04-01) |
| Invoices by Azure subscription | `GET /providers/Microsoft.Billing/billingAccounts/default/billingSubscriptions/{subscriptionId}/invoices` | Subscription owner/reader | B | MCA/MPA | [src](https://learn.microsoft.com/en-us/rest/api/billing/invoices/list-by-billing-subscription?view=rest-billing-2024-04-01) |
| Invoice PDF | `POST .../invoices/{name}/download[?documentName=]` | Invoice manager / reader | B,C,E | MCA, MPA, EA. 200 `{url, expiryTime}` or 202 + `Location` + `Retry-After` → poll. **Never persist the URL** | [src](https://learn.microsoft.com/en-us/rest/api/billing/invoices/download-by-billing-account?view=rest-billing-2024-04-01) |
| Multi-document ZIP | `POST .../billingAccounts/{ba}/downloadDocuments` body `[{"documentName": "...", "invoiceName": "..."}]` | Invoice manager / reader | B,E | **MCA and MPA.** Omitting `documentName` returns the invoice PDF. 200 `{url, expiryTime}` or 202 + `Location` + `Retry-After` | [src](https://learn.microsoft.com/en-us/rest/api/billing/invoices/download-documents-by-billing-account?view=rest-billing-2024-04-01) |
| Invoice summary / transactions CSV | `POST .../invoices/{name}/downloadSummary`, `POST .../invoices/{name}/transactionsDownload` | Reader | C | **EA only** | [summary](https://learn.microsoft.com/en-us/rest/api/billing/invoices/download-summary-by-billing-account?view=rest-billing-2024-04-01), [transactions](https://learn.microsoft.com/en-us/rest/api/billing/transactions/transactions-download-by-invoice?view=rest-billing-2024-04-01) |
| **Transactions by billing profile (prices)** | `GET .../billingProfiles/{p}/transactions?periodStartDate=&periodEndDate=&type=Billed\|Unbilled&top=50` | Reader | B,E | `periodStartDate`, `periodEndDate` and `type` are **required**; `top` ≤ 50, page via `nextLink`. Per line: `transactionType`, `kind`, `date`, `invoice`, `productDescription`, `productFamily`, `productTypeId`, `quantity`, `unitOfMeasure`, `effectivePrice`, `marketPrice`, `subTotal`, `tax`, `transactionAmount`, `azureCreditApplied`, `servicePeriodStartDate`, `servicePeriodEndDate`, `isThirdParty`, `pricingCurrency`, `billingCurrency`. **Unbilled transactions are listed under the pending invoice and exclude tax.** Seat-based M365 lines appear here for MCA | [src](https://learn.microsoft.com/en-us/rest/api/billing/transactions/list-by-billing-profile?view=rest-billing-2024-04-01) |
| Transactions by invoice | `GET .../invoices/{name}/transactions` | Reader | B,E | Optional filters only | [src](https://learn.microsoft.com/en-us/rest/api/billing/transactions/list-by-invoice?view=rest-billing-2024-04-01) |
| Transaction summary | `GET .../invoices/{name}/transactionSummary?filter=&search=` | Reader | B,E | `subTotal`, `tax`, `total`, `azureCreditApplied` | [src](https://learn.microsoft.com/en-us/rest/api/billing/transactions/get-transaction-summary-by-invoice?view=rest-billing-2024-04-01) |
| Billing role assignments | `GET .../billingRoleAssignments` | Any billing role | B,C,E | Lists the **caller's** assignments — exactly what the onboarding probe needs | [src](https://learn.microsoft.com/en-us/rest/api/billing/billing-role-assignments/list-by-billing-account?view=rest-billing-2024-04-01) |
| Billing role definitions | `GET .../billingRoleDefinitions?api-version=2020-05-01` | Any billing role | B,E | **Only published for 2020-05-01** — do not send 2024-04-01 | [src](https://learn.microsoft.com/en-us/rest/api/billing/billing-role-definitions/list-by-billing-account?view=rest-billing-2020-05-01) |

**Price sheets** are `Microsoft.CostManagement` operations under billing scopes (see §2.3).

### 2.3 `Microsoft.CostManagement` — api-version `2026-06-01` (2025-03-01 still supported)

`{scope}` ∈ `/subscriptions/{id}` · `/subscriptions/{id}/resourceGroups/{rg}` · `/providers/Microsoft.Management/managementGroups/{mg}` · `/providers/Microsoft.Billing/billingAccounts/{ba}` · `.../billingProfiles/{p}` · `.../invoiceSections/{s}` · `.../departments/{d}` · `.../enrollmentAccounts/{e}` · `.../customers/{c}` (MPA).

**Scope support** ([understand-work-scopes](https://learn.microsoft.com/en-us/azure/cost-management-billing/costs/understand-work-scopes)): management groups are **not supported for MCA subscriptions** and **not supported in CSP scopes**; management groups include **only usage-based charges** (purchases need the billing scope); Cost Management supports CSP customers only when they have an MCA. MCA billing-scope access comes from billing roles, and the billing profile's **Azure charges** policy must be on ([assign access](https://learn.microsoft.com/en-us/azure/cost-management-billing/costs/assign-access-acm-data)). EA management-group/subscription views need **AO view charges** ("Account owners can view charges"). Scope selection: ADR-017 ladder.

| Purpose | Request | Access | Notes | Source |
|---|---|---|---|---|
| **Query** | `POST {scope}/providers/Microsoft.CostManagement/query` | Cost Management Reader (RBAC) or a billing role (billing scopes) | Body: `type` (Usage/ActualCost/AmortizedCost), `timeframe` (MonthToDate/BillingMonthToDate/TheLastMonth/TheLastBillingMonth/WeekToDate/TheCurrentMonth/Custom), `timePeriod{from,to}`, `dataset{granularity, aggregation, grouping, filter}`. **Granularity:** the enum is `Daily \| Monthly`, but the docs state "Currently 'Daily' is supported for most cases"; MLCP sends `Daily`, or omits granularity for totals (several official samples send `None`). Max **2 groupings** and **2 aggregations**. Page via `properties.nextLink`. **Header `ClientType: Mlcp`** (see limits) | [src](https://learn.microsoft.com/en-us/rest/api/cost-management/query/usage?view=rest-cost-management-2026-06-01) |
| Forecast | `POST {scope}/providers/Microsoft.CostManagement/forecast` | Same | `timeframe` = `Custom` only; `includeActualCost`, `includeFreshPartialCost`; may return 204 | [src](https://learn.microsoft.com/en-us/rest/api/cost-management/forecast/usage?view=rest-cost-management-2026-06-01) |
| Dimensions | `GET {scope}/providers/Microsoft.CostManagement/dimensions` | Same | `$filter` on category/usage dates, `$expand=properties/data`, `$top` ≤ 1000 | [src](https://learn.microsoft.com/en-us/rest/api/cost-management/dimensions/list?view=rest-cost-management-2026-06-01) |
| Cost details (on-demand CSV) | `POST {scope}/providers/Microsoft.CostManagement/generateCostDetailsReport` body `{metric: ActualCost\|AmortizedCost, timePeriod{start,end}}` or `invoiceId` (MCA, billing profile/customer scope) or `billingPeriod` `YYYYMM` (EA) — mutually exclusive | Same | 202 + `Location` + `Retry-After` → poll the **returned `Location` verbatim** (`.../costDetailsOperationResults/{id}`) → 200 `manifest.blobs[].blobLink`, `validTill` (~1 h), `status` Completed/NoDataFound/Failed. Range ≤ 1 month, ≤ 13 months back. **Resource group and management group scopes are not supported** (for EA or MCA). "Rate limited" (no published numbers). EA and MCA only. **Recommended ≤ 1 call per day per scope**; use Exports above ~2 GB/month | [create](https://learn.microsoft.com/en-us/rest/api/cost-management/generate-cost-details-report/create-operation?view=rest-cost-management-2026-06-01), [results](https://learn.microsoft.com/en-us/rest/api/cost-management/generate-cost-details-report/get-operation-results?view=rest-cost-management-2026-06-01), [guide](https://learn.microsoft.com/en-us/azure/cost-management-billing/automate/get-small-usage-datasets-on-demand) |
| Price sheet (MCA/MPA, current month) | `POST /providers/Microsoft.Billing/billingAccounts/{ba}/billingProfiles/{p}/providers/Microsoft.CostManagement/pricesheets/default/download` | Profile reader | **Current month only.** 200 `{downloadUrl, expiryTime}` or 202 + `Location`. Zip of CSV/JSON (≤ 75 MB each). Azure meters | [src](https://learn.microsoft.com/en-us/rest/api/cost-management/price-sheet/download-by-billing-profile?view=rest-cost-management-2026-06-01) |
| Price sheet (MCA/MPA, by invoice) | `POST .../billingProfiles/{p}/invoices/{invoice}/providers/Microsoft.CostManagement/pricesheets/default/download` | Profile reader | Past periods | [src](https://learn.microsoft.com/en-us/rest/api/cost-management/price-sheet/download-by-invoice?view=rest-cost-management-2026-06-01) |
| Price sheet (EA) | `POST /providers/Microsoft.Billing/billingAccounts/{ba}/billingPeriods/{period}/providers/Microsoft.CostManagement/pricesheets/default/download` | Enrollment reader | Past 13 billing periods. Replaces the Microsoft.Consumption EA price sheet download, retired 1 June 2026 | [src](https://learn.microsoft.com/en-us/rest/api/cost-management/price-sheet/download-by-billing-account?view=rest-cost-management-2026-06-01) |
| Exports | `PUT {scope}/providers/Microsoft.CostManagement/exports/{name}` | Contributor | To Azure Blob; CSV or Parquet; optional; for very large tenants | [src](https://learn.microsoft.com/en-us/rest/api/cost-management/exports/create-or-update?view=rest-cost-management-2026-06-01) |
| Alerts | `GET {scope}/providers/Microsoft.CostManagement/alerts` | Reader | Budget + anomaly alerts | [src](https://learn.microsoft.com/en-us/rest/api/cost-management/alerts/list?view=rest-cost-management-2026-06-01) |
| Benefit utilisation | `GET .../billingAccounts/{ba}/providers/Microsoft.CostManagement/benefitUtilizationSummaries` (EA), `.../billingProfiles/{p}/...` (MCA), `/providers/Microsoft.BillingBenefits/savingsPlanOrders/{o}/savingsPlans/{s}/...` | Reader | `grainParameter` Daily or Monthly | [src](https://learn.microsoft.com/en-us/rest/api/cost-management/benefit-utilization-summaries?view=rest-cost-management-2026-06-01) |
| Cost allocation rules | `GET /providers/Microsoft.Billing/billingAccounts/{ba}/providers/Microsoft.CostManagement/costAllocationRules` | Billing admin | Read-only; Phase 4 optional | [src](https://learn.microsoft.com/en-us/rest/api/cost-management/cost-allocation-rules/list?view=rest-cost-management-2026-06-01) |

**Throttling and limits**

| Limit | Value | Status | Source |
|---|---|---|---|
| Query API quota (per tenant) | **12 QPU / 10 s, 60 QPU / 1 min, 600 QPU / 1 h**; 1 QPU ≈ one month of data queried | Documented | [manage-automation](https://learn.microsoft.com/en-us/azure/cost-management-billing/costs/manage-automation) |
| Query API headers | `x-ms-ratelimit-microsoft.costmanagement-qpu-retry-after` (back off), `-qpu-consumed`, `-qpu-remaining` | Documented | [manage-automation](https://learn.microsoft.com/en-us/azure/cost-management-billing/costs/manage-automation) |
| 429 on Cost Management operations | `x-ms-ratelimit-microsoft.consumption-retry-after`; 503 → `Retry-After` | Documented (REST error definitions) | [query](https://learn.microsoft.com/en-us/rest/api/cost-management/query/usage?view=rest-cost-management-2026-06-01), [alerts](https://learn.microsoft.com/en-us/rest/api/cost-management/alerts/list?view=rest-cost-management-2026-06-01) |
| Call frequency | "call the APIs no more than once per day"; data refreshes every ~4 h | Recommendation | [manage-automation](https://learn.microsoft.com/en-us/azure/cost-management-billing/costs/manage-automation) |
| `ClientType` header | Requests without it share one 2000 calls/min bucket with every other client that omits it | **Q&A** (not in product docs) | [Q&A](https://learn.microsoft.com/en-us/answers/questions/1340993/exception-429-too-many-requests-for-azure-cost-man) |
| Per-scope / per-tenant / per-ClientType | ~4 calls/min per scope, ~20/min per tenant, 2000/min per ClientType; also `x-ms-ratelimit-microsoft.costmanagement-{entity\|tenant\|client}-retry-after` | **Q&A** | [Q&A](https://learn.microsoft.com/en-us/answers/questions/1340993/exception-429-too-many-requests-for-azure-cost-man) |

Design: ADR-017 scope ladder; server-side grouping; per-subscription queries only on rung 3, sequential, under the QPU budget; honour all three retry headers. **Data refresh ~4 h; new resources up to 24 h.**

### 2.4 `Microsoft.Consumption` — api-version `2026-06-01`

**Status:** the Consumption APIs are **in maintenance mode and on a path to deprecation**; Usage Details and Marketplaces are "Maintenance mode. Will be deprecated in the future" with no date yet; prefer the Cost Management equivalents ([transition guide](https://learn.microsoft.com/en-us/azure/cost-management-billing/automate/transition-consumption-apis-cost-management-apis), [usage details migration](https://learn.microsoft.com/en-us/azure/cost-management-billing/automate/migrate-consumption-usage-details-api), [marketplaces migration](https://learn.microsoft.com/en-us/azure/cost-management-billing/automate/migrate-consumption-marketplaces-api)). The EA **price sheet download** moved to Cost Management (§2.3; Consumption version retired 1 June 2026). MLCP does not use UsageDetails or Marketplaces.

| Purpose | Request | RBAC | Notes | Source |
|---|---|---|---|---|
| Budgets | `GET/PUT {scope}/providers/Microsoft.Consumption/budgets/{name}` | Reader / Contributor | Read in Phase 4; write deferred | [get](https://learn.microsoft.com/en-us/rest/api/consumption/budgets/get?view=rest-consumption-2026-06-01), [put](https://learn.microsoft.com/en-us/rest/api/consumption/budgets/create-or-update?view=rest-consumption-2026-06-01) |
| Reservation recommendations | `GET {scope}/providers/Microsoft.Consumption/reservationRecommendations?$filter=properties/lookBackPeriod eq 'Last30Days'` | Reader | Also `properties/scope`, `properties/resourceType`; may return 204 | [src](https://learn.microsoft.com/en-us/rest/api/consumption/reservation-recommendations/list?view=rest-consumption-2026-06-01) |
| Reservation recommendation details | `GET {scope}/providers/Microsoft.Consumption/reservationRecommendationDetails?scope=&region=&term=&lookBackPeriod=&product=` | Reader | Same api-version as the rest of this section | [src](https://learn.microsoft.com/en-us/rest/api/consumption/reservation-recommendation-details/get?view=rest-consumption-2026-06-01) |
| Reservation details | `GET {scope}/providers/Microsoft.Consumption/reservationDetails` | Reader | 12 MB payload limit; narrow date ranges | [src](https://learn.microsoft.com/en-us/rest/api/consumption/reservations-details/list?view=rest-consumption-2026-06-01) |
| Reservation summaries | `GET {scope}/providers/Microsoft.Consumption/reservationSummaries?grain=monthly` | Reader | `grain` = `daily` \| `monthly`; `$filter` required for daily | [src](https://learn.microsoft.com/en-us/rest/api/consumption/reservations-summaries/list?view=rest-consumption-2026-06-01) |
| Throttling | 429 → `x-ms-ratelimit-microsoft.consumption-retry-after`; 503 → `Retry-After` | | Handled in the Polly policy | [src](https://learn.microsoft.com/en-us/rest/api/consumption/reservation-recommendation-details/get?view=rest-consumption-2026-06-01) |

### 2.5 Azure Advisor — api-version `2025-01-01` (latest GA)

`GET /subscriptions/{id}/providers/Microsoft.Advisor/recommendations?$filter=Category eq 'Cost'` — Reader. Page via `nextLink`. Phase 4. Re-check the latest GA api-version when implementing (a 2026 preview exists). [src](https://learn.microsoft.com/en-us/rest/api/advisor/recommendations/list)

### 2.6 Azure Retail Prices — `https://prices.azure.com/api/retail/prices?api-version=2023-01-01-preview&$filter=...`

Unauthenticated; Azure meters only; ≤ 1,000 items per page via `NextPageLink`; filter values are case-sensitive; USD is the pricing currency (other currencies are for reference). Used for "at list price" comparisons. [src](https://learn.microsoft.com/en-us/rest/api/cost-management/retail-prices/azure-retail-prices)

### 2.7 Customer-side grants (what customers do in their tenant)

| Grant | How | Source |
|---|---|---|
| Cost Management Reader at a management group | Owner / User Access Administrator / Role Based Access Control Administrator on that scope; one-click via the **Deploy to Azure** button `https://portal.azure.com/#create/Microsoft.Template/uri/<url-encoded raw URL of infra/customer-rbac.json>` (ARM JSON only; the URL must be publicly reachable; management-group templates are supported) | [role assignment](https://learn.microsoft.com/en-us/azure/role-based-access-control/role-assignments-portal), [button](https://learn.microsoft.com/en-us/azure/azure-resource-manager/templates/deploy-to-azure-button), [ACM access](https://learn.microsoft.com/en-us/azure/cost-management-billing/costs/assign-access-acm-data) |
| The principal to grant | The **MLCP enterprise application's Object ID** (service principal), from *Entra ID → Enterprise apps → All applications → MLCP → Properties* — not the Application (client) ID | [properties](https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/application-properties) |
| MCA billing roles | *Cost Management + Billing → Billing scopes → (account or profile) → Access control (IAM) → Add* → role → search for the user, group **or app**. Billing account reader at the account; **Invoice manager exists only at billing-profile scope** | [MCA roles](https://learn.microsoft.com/en-us/azure/cost-management-billing/manage/understand-mca-roles) |
| Global Administrator elevation (MCA/MPA only) | *Cost Management + Billing → Billing scopes* → view all billing accounts → account → *Access control (IAM)* → add self as Billing account owner. Not available for EA or MOSP | [elevate](https://learn.microsoft.com/en-us/azure/cost-management-billing/manage/elevate-access-global-admin) |
| EA roles for a service principal | Only via REST (`EnrollmentReader`, `DepartmentReader`, …, api-version `2019-10-01-preview`); not shown in the portal; assigned by an enrollment writer | [src](https://learn.microsoft.com/en-us/azure/cost-management-billing/manage/assign-roles-azure-service-principals) |

---

## 3. Partner Center — deferred to Phase 6

| Fact | Source |
|---|---|
| `https://api.partnercenter.microsoft.com/v1`; app-only preferred; **App+User calls require MFA from 1 April 2026** | [auth](https://learn.microsoft.com/en-us/partner-center/developer/partner-center-authentication) |
| API access requires a CSP tenant that is an Indirect Provider or direct-bill partner — **indirect resellers have no API access** | [setup](https://learn.microsoft.com/en-us/partner-center/developer/set-up-api-access-in-partner-center) |
| NCE licence-based products: prorated cancellation within **seven calendar days** of purchase or renewal | [policy](https://learn.microsoft.com/en-us/partner-center/customers/new-commerce-cancellation-policy) |
| Reconciliation exports on Graph v1.0 (`billedUsage`, `unbilledUsage`, `billedReconciliation`, `unbilledReconciliation` → `export` → 202 + `Location` → poll → manifest); `PartnerBilling.Read.All`; CSP partners only, caller in the partner tenant | [overview](https://learn.microsoft.com/en-us/graph/api/resources/partners-billing-api-overview?view=graph-rest-1.0) |

---

## 4. Identity platform

| Item | Value | Source |
|---|---|---|
| Registrations | Two multi-tenant registrations (`AzureADMultipleOrgs`) under one verified publisher: **MLCP** (core) and **MLCP Usage Insights** (ADR-015; runbook `infra/entra.md`) | — |
| Publisher verification | Required for both. CPP account (PGA Partner One ID); verifier holds Application Administrator or Cloud Application Administrator in Entra and CPP Partner Admin / Account Admin; MFA; publisher domain not `*.onmicrosoft.com` | [overview](https://learn.microsoft.com/en-us/entra/identity-platform/publisher-verification-overview), [how-to](https://learn.microsoft.com/en-us/entra/identity-platform/mark-app-as-publisher-verified) |
| Sign-in | OIDC authorization code + PKCE (Microsoft.Identity.Web), core app; code redemption with the Key Vault certificate | — |
| Admin consent URL | `https://login.microsoftonline.com/organizations/v2.0/adminconsent?client_id={app}&scope=https://graph.microsoft.com/.default&redirect_uri=…&state=…`. **`/.default` requests every application permission configured on that registration** — hence one URL per registration. Returns `admin_consent`, `tenant`, `scope`, `state` (or `error`, `error_description`). The `tenant` value must not be used to authorise — MLCP verifies with an app-only call (ADR-018) | [src](https://learn.microsoft.com/en-us/entra/identity-platform/v2-admin-consent) |
| Who can consent | Microsoft Graph **application** permissions: Global Administrator or **Privileged Role Administrator**. Cloud Application Administrator / Application Administrator can consent to anything *except* Graph app roles | [src](https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/grant-admin-consent) |
| Admin detection (`wids`) | Core registration sets `groupMembershipClaims: "DirectoryRole"` so tokens carry `wids` (tenant-wide role template IDs). Documented for access tokens; ID-token emission is shown in the Zero Trust guide — verify with a test tenant. Owner = `wids` contains `62e90394-69f5-4237-9190-012177145e10` (Global Administrator) or `e8611ab8-c189-46e8-94e1-60213ab1f814` (Privileged Role Administrator) (ADR-018) | [access token claims](https://learn.microsoft.com/en-us/entra/identity-platform/access-token-claims-reference), [group claims](https://learn.microsoft.com/en-us/security/zero-trust/develop/configure-tokens-group-claims-app-roles), [manifest](https://learn.microsoft.com/en-us/entra/identity-platform/reference-microsoft-graph-app-manifest) |
| App role | `SubscriptionManager` on the core registration; arrives in the `roles` claim (ADR-023) | — |
| App-only tokens | Client credentials with the certificate; per (tenant, app, audience) at `https://login.microsoftonline.com/{tid}/oauth2/v2.0/token`; scope `https://graph.microsoft.com/.default` or `https://management.azure.com/.default` | [src](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-client-creds-grant-flow) |
| Delegated user actions | No On-Behalf-Of in the single deployable; incremental consent + MSAL distributed cache (Phase 5 writes only) | — |
| Token cache | Azure Managed Redis (Entra auth), MSAL serialisation encrypted with Data Protection keys wrapped by Key Vault | — |

### 4.1 Failure classification (ADR-016)

Classified once in `Mlcp.Shared.Resilience` (`MicrosoftFailureKind`). Only the first two kinds touch `Tenant.Status`. Error-code meanings: [AADSTS reference](https://learn.microsoft.com/en-us/entra/identity-platform/reference-error-codes).

| Kind | Signals | Tenant | Capability | Retry |
|---|---|---|---|---|
| **GrantRevoked** | Core app token endpoint: `AADSTS700016` (app not found in the tenant) / `AADSTS7000229` (service principal missing — Q&A-described, not in the reference table) **after** the 10-minute propagation window; `AADSTS7000112` (application disabled); `AADSTS90002` (tenant not found); `invalid_grant`. **401** on a core floor call with a freshly acquired token | → `NeedsReconsent` | Floor → `ConsentRevoked` | No |
| **FloorPermissionRemoved** | **403** on `subscribedSkus`, `directory/subscriptions`, `organization` | → `NeedsReconsent` | `GraphLicensing` → `ConsentRevoked` | No |
| **CapabilityDenied** | 401/403 on usage reports, ARM, Cost Management, Billing; token-endpoint consent errors for the **Usage Insights** app | none | → `Tier2NotGranted` / `RbacMissing` / `BillingRoleMissing` / `RoleRevoked` | No |
| **Transient** | 408, 429, 5xx, timeouts, open circuit, network errors, token endpoint 5xx; `AADSTS700016`/`7000229` within 10 min of the consent callback | none | Previous verdict kept, marked stale (`ProviderError`) | Yes |
| **PlatformCredential** | `AADSTS7000215` (invalid client secret), `AADSTS7000222` (expired client secret keys), `AADSTS700027` (client assertion failed signature validation), `invalid_client`, Key Vault certificate load failure | none — all sync for that app paused | none | Ops alert (`Critical`); retry after 5 min |
| **NotFound / other 4xx** | 404, 400 | none | Probe-specific (e.g. `NoBillingAccount`) | No |

`AADSTS65001` (delegation does not exist) applies to delegated flows only and never flags a tenant. On any 401/403 the cached token for that (tenant, app, audience) is evicted. With client credentials a removed application permission still yields a token (without the role), so the refusal surfaces as 401/403 from the API — which is why floor-call status codes are part of the signals.
