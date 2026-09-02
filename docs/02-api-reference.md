# 02 — Microsoft API Reference (Authoritative for Implementation)

Legend: ✅ supported · ⚠️ conditional · ❌ unavailable. Scenario letters per `01-scope-and-scenarios.md` §4.
Rule: **do not use an endpoint, permission, or field not listed here without adding it with a Microsoft Learn source link.**

---

## 1. Microsoft Graph (v1.0 unless noted) — `https://graph.microsoft.com`

### 1.1 Licensing & directory

| Purpose | Request | Least-privilege permission | App / Delegated | Notes |
|---|---|---|---|---|
| SKU seat position | `GET /subscribedSkus` | `Organization.Read.All` | Both | `$select` only, **no `$filter`**. Fields: `skuId`, `skuPartNumber`, `appliesTo`, `capabilityStatus`, `consumedUnits`, `prepaidUnits{enabled,suspended,warning,lockedOut}`, `servicePlans[]{servicePlanId,servicePlanName,provisioningStatus,appliesTo}`, `subscriptionIds[]`. |
| Subscription inventory | `GET /directory/subscriptions` | `Organization.Read.All` | Both | `companySubscription`: `id`, `commerceSubscriptionId`, `createdDateTime`, `isTrial`, `nextLifecycleDateTime`, `skuId`, `skuPartNumber`, `status` (Enabled/Deleted/Suspended/Warning/LockedOut), `totalLicenses`, `ownerId`, `ownerTenantId`, `ownerType`, `serviceStatus[]`. **No price, no auto-renew, no term.** `ownerTenantId` set ⇒ partner-created (scenario D signal). |
| Users + assignments (bulk) | `GET /users?$select=id,userPrincipalName,displayName,accountEnabled,department,companyName,officeLocation,usageLocation,assignedLicenses&$top=999` | `User.Read.All` | Both | Page via `@odata.nextLink`. Use `/users/delta` with same `$select` for incremental. |
| Per-user licence detail | `GET /users/{id}/licenseDetails` | `User.Read.All` | Both | Only when service-plan-level detail needed; batch via `POST /$batch` (20/req). |
| Assign / remove licence | `POST /users/{id}/assignLicense` | `User.ReadWrite.All` | Both | Phase 5. Assigns existing seats only. |
| Group-based licensing | `POST /groups/{id}/assignLicense` | `Group.ReadWrite.All` | Both | Requires Entra ID P1. Phase 5. |
| Organization | `GET /organization` | `Organization.Read.All` | Both | Display name, verified domains, `tenantType`. |

### 1.2 Usage reports (tier 2 unlock)

All: `Reports.Read.All`, both auth types. Delegated additionally requires an Entra reporting-capable role. Response is **302 → pre-authenticated CSV URL valid a few minutes**. Periods: `D7`, `D30`, `D90`, `D180`. Data lags ~24–48h.

| Report | Request |
|---|---|
| M365 active users (all workloads) | `GET /reports/getOffice365ActiveUserDetail(period='D90')` |
| Teams | `GET /reports/getTeamsUserActivityUserDetail(period='D90')` |
| Exchange | `GET /reports/getEmailActivityUserDetail(period='D90')` |
| SharePoint | `GET /reports/getSharePointActivityUserDetail(period='D90')` |
| OneDrive | `GET /reports/getOneDriveActivityUserDetail(period='D90')` |
| M365 Apps | `GET /reports/getM365AppUserDetail(period='D90')` |
| Copilot | `GET /reports/getMicrosoft365CopilotUsageUserDetail(period='D90')` |
| Activations | `GET /reports/getOffice365ActivationsUserDetail` |
| Anonymisation setting | `GET/PATCH /beta/admin/reportSettings` — `displayConcealedNames` — `ReportSettings.ReadWrite.All` — **beta**; tenant-wide privacy setting; never change silently |

**Anonymisation:** on by default since Sept 2021. When on, user identifiers in reports are hashed and cannot be joined to directory users. Aggregate metrics work; per-user/department views require admin opt-in (feature flag `UsageInsights.IdentifiableNames`).

### 1.3 Graph throttling
Per-app-per-tenant limits; `429` with `Retry-After`. Use `$batch`, delta, `$select`. Stagger tenant sync by tenant-ID hash.

---

## 2. Azure Resource Manager — `https://management.azure.com`

Authorisation is **Azure RBAC** (subscriptions/MGs) and **billing roles** (billing scopes), not Graph scopes. Admin consent grants nothing here. See `03-architecture.md` §4.

### 2.1 Subscriptions & classification

| Purpose | Request | Role | Notes |
|---|---|---|---|
| List Azure subscriptions | `GET /subscriptions?api-version=2022-12-01` | Reader | |
| Agreement type per subscription | `GET /subscriptions/{id}/providers/Microsoft.Billing/billingProperty/default?api-version=2024-04-01` | Reader | `billingAccountAgreementType`, `billingProfileId`, `invoiceSectionId`, `costCenter`, `subscriptionBillingStatus`. PATCH sets `costCenter`. |
| Management groups | `GET /providers/Microsoft.Management/managementGroups?api-version=2021-04-01` | MG Reader | For root-scope RBAC assignment guidance. |

### 2.2 `Microsoft.Billing` — api-version `2024-04-01`

Base: `/providers/Microsoft.Billing/billingAccounts/{billingAccountName}`

| Purpose | Request | Billing role | Scenario | Notes |
|---|---|---|---|---|
| Billing accounts | `GET /providers/Microsoft.Billing/billingAccounts` | Any billing role | B,C,E | `agreementType`: `MicrosoftCustomerAgreement` \| `EnterpriseAgreement` \| `MicrosoftPartnerAgreement` \| `MicrosoftOnlineServicesProgram`. `accountType`. |
| Billing profiles | `GET .../billingProfiles` | Profile reader | B,E | |
| Invoice sections | `GET .../billingProfiles/{p}/invoiceSections` | Profile reader | B,E | |
| Associated tenants | `GET .../associatedTenants` | Account owner | B | `billingManagementState`, `provisioningManagementState`. |
| **Billing subscriptions** | `GET .../billingSubscriptions?includeTenantSubscriptions=true&includeDeleted=false` | Account/profile reader | B,C,E | Header `x-ms-service-tenantinfo: true`. Fields: `autoRenew` (On/Off), `billingFrequency`, `productCategory` (SeatBased/UsageBased/Software/ReservationOrder/SavingsPlanOrder/Other), `productType`, `productTypeId`, `skuId`, `skuDescription`, `quantity`, `purchaseDate`, `termDuration`, `termStartDate`, `termEndDate`, `nextBillingCycleDetails`, `renewalTermDetails`, `systemOverrides{cancellation, cancellationAllowedEndDate}`, `provisioningTenantId`, `status`, `suspensionReasons[]`, `reseller`, `subscriptionId` (Azure sub GUID for usage-based), `enrollmentAccountStatus` (EA). `expand=LastMonthCharges,MonthToDateCharges` (usage-based only). |
| By profile / section / customer / enrolment | `.../billingProfiles/{p}/billingSubscriptions`, `.../invoiceSections/{s}/billingSubscriptions`, `.../customers/{c}/billingSubscriptions` (MPA), `.../enrollmentAccounts/{e}/billingSubscriptions` (EA) | Scope reader | | |
| Update auto-renew | `PATCH .../billingSubscriptions/{id}` body `{"properties":{"autoRenew":"Off"}}` | Profile contributor | B | Phase 5. Delegated only. |
| Create seat-based subscription | `PUT .../billingSubscriptionAliases/{guid}` body `{"properties":{"skuId","quantity","termDuration","billingFrequency","displayName","billingProfileId","invoiceSectionId"}}` | Profile owner/contributor | B | Phase 5. **Purchase.** 201/202 async. |
| Reduce quantity | `POST .../billingSubscriptions/{id}/split` | Profile contributor | B | Phase 5. Subject to `systemOverrides`. |
| Merge | `POST .../billingSubscriptions/{id}/merge` | Profile contributor | B | Phase 5. |
| Cancel seat-based | `DELETE .../billingSubscriptions/{id}` | Profile owner | B,E | Phase 5. Only within `cancellationAllowedEndDate`. |
| Cancel usage-based | `POST .../billingSubscriptions/{id}/cancel` | Admin agent | E | MPA only. |
| Move invoice section | `POST .../billingSubscriptions/{id}/move` (+ `/validateMoveEligibility`) | Profile contributor | B | |
| Products (reservations, software) | `GET .../products` | Reader | B,E | `autoRenew`, `lastCharge`, `status`. |
| **Invoices list** | `GET .../invoices?periodStartDate=YYYY-MM-DD&periodEndDate=YYYY-MM-DD` | Reader / invoice manager | B,C,E | All agreement types. Also `.../billingProfiles/{p}/invoices`, `/providers/Microsoft.Billing/billingAccounts/default/billingSubscriptions/{sub}/invoices`. |
| Invoice PDF | `POST .../invoices/{name}/download` | Invoice manager | B,C,E | 202 + `Location` + `Retry-After` → poll → `{url, expiryTime}` SAS. **Never persist.** |
| Multi-doc ZIP | `POST .../invoices/{name}/downloadDocuments` body `[{documentName}]` | Invoice manager | B,E | PDF, credit notes, tax receipts. |
| Invoice summary / transactions CSV | `POST .../invoices/{name}/downloadSummary`, `.../transactionsDownload` | Reader | C | EA only. |
| **Transactions (prices)** | `GET .../invoices/{name}/transactions`; `GET .../billingProfiles/{p}/transactions?startDate=&endDate=&type=billed|unbilled`; `.../invoiceSections/{s}/transactions`; `.../customers/{c}/transactions` (MPA) | Reader | B,E | Per line: `transactionType`, `kind` (Billed/Unbilled), `date`, `invoice`, `productDescription`, `productFamily`, `productType`, `productTypeId`, `quantity`, `units`, `unitOfMeasure`, `unitType`, `effectivePrice{currency,value}`, `marketPrice`, `subTotal`, `tax`, `transactionAmount`, `azureCreditApplied`, `consumptionCommitmentDecremented`, `servicePeriodStartDate`, `servicePeriodEndDate`, `isThirdParty`, `pricingCurrency`, `billingCurrency`, `refundTransactionDetails`. **Unbilled excludes tax.** Seat-based M365 lines appear here for MCA. |
| Transaction summary | `GET .../invoices/{name}/transactionSummary?filter=&search=` | Reader | B,E | |
| Price sheet | `POST .../billingProfiles/{p}/providers/Microsoft.CostManagement/pricesheets/default/download` | Profile reader | B,C | Azure meters only. 13 months. |
| Billing roles | `GET .../billingRoleAssignments`, `.../billingRoleDefinitions` | Account reader | B,E | Used by onboarding probe to explain missing roles. |

### 2.3 `Microsoft.CostManagement` — api-version `2025-03-01` (Query/Forecast may use `2024-08-01`)

`{scope}` ∈ `/subscriptions/{id}` · `/subscriptions/{id}/resourceGroups/{rg}` · `/providers/Microsoft.Management/managementGroups/{mg}` · `/providers/Microsoft.Billing/billingAccounts/{ba}` · `.../billingProfiles/{p}` · `.../invoiceSections/{s}` · `.../departments/{d}` · `.../enrollmentAccounts/{e}` · `.../customers/{c}` (MPA)

| Purpose | Request | RBAC | Notes |
|---|---|---|---|
| **Query** | `POST {scope}/providers/Microsoft.CostManagement/query` | Cost Management Reader | Body: `type` (ActualCost/AmortizedCost/Usage), `timeframe` (MonthToDate/Custom/…), `timePeriod{from,to}`, `dataset{granularity: Daily|Monthly|None, aggregation{totalCost:{name:"Cost",function:"Sum"}, totalCostUSD}, grouping[{type:"Dimension"\|"TagKey", name}], filter}`. Dimensions: `SubscriptionId`, `SubscriptionName`, `ResourceGroupName`, `ResourceId`, `ResourceType`, `ResourceLocation`, `ServiceName`, `ServiceTier`, `MeterCategory`, `MeterSubCategory`, `Meter`, `PricingModel`, `ChargeType`, `PublisherType`, `Provider`, `BillingProfileId`, `InvoiceSectionId`, `CostCenter`… **Header `ClientType: Mlcp` required.** Paginate via `nextLink`. |
| Forecast | `POST {scope}/providers/Microsoft.CostManagement/forecast` | Reader | Same shape; `includeActualCost`, `includeFreshPartialCost`. |
| Dimensions | `GET {scope}/providers/Microsoft.CostManagement/dimensions` | Reader | Drive dynamic grouping UI. |
| Cost details (on-demand CSV) | `POST {scope}/providers/Microsoft.CostManagement/generateCostDetailsReport?api-version=2025-03-01` body `{metric: ActualCost|AmortizedCost, timePeriod{start,end}}` (or `invoiceId` for MCA, `billingPeriod` YYYYMM for EA) | Reader | 202 + `Location` + `Retry-After` → `GET .../costDetailsOperationStatus/{op}` → `manifest.blobs[].blobLink`, `validTill` (~1h). **≤1 request/day/scope+range.** Fields incl. `Date`, `SubscriptionId`, `ResourceGroup`, `ResourceId`, `ResourceName`, `MeterCategory`, `MeterName`, `Quantity`, `UnitOfMeasure`, `CostInBillingCurrency`, `BillingCurrency`, `ChargeType`, `PricingModel`, `Tags`. |
| Exports | `PUT {scope}/providers/Microsoft.CostManagement/exports/{name}` | Contributor | Recurring to storage. Optional; use for very large tenants. |
| Alerts | `GET {scope}/providers/Microsoft.CostManagement/alerts` | Reader | Budget + anomaly alerts. |
| Benefit utilisation | `GET {scope}/providers/Microsoft.CostManagement/benefitUtilizationSummaries` | Reader | |
| Cost allocation rules | `GET .../costAllocationRules` | Billing admin | Read-only; Phase 4 optional. |

**Rate limits (support-reported, not formally published):** ~4 calls/min/scope, ~20/min/tenant, ~2000/min/ClientType. Design: query at billing/MG scope grouped by `SubscriptionId`/`ResourceGroupName`; cache; 429 → `Retry-After`. **Data refresh ~4h; new resources up to 24h.**

### 2.4 `Microsoft.Consumption` — api-version `2024-08-01` (`2026-06-01` for reservation recommendation details)

| Purpose | Request | RBAC | Notes |
|---|---|---|---|
| Budgets | `GET/PUT {scope}/providers/Microsoft.Consumption/budgets/{name}` | Reader / Contributor | Read Phase 4; write deferred. |
| Reservation recommendations | `GET {scope}/providers/Microsoft.Consumption/reservationRecommendations?$filter=properties/lookBackPeriod eq 'Last30Days'` | Reader | |
| Reservation details / summaries | `GET .../reservationDetails`, `.../reservationSummaries?grain=monthly` | Reader | |
| Marketplace charges | `GET {scope}/providers/Microsoft.Consumption/marketplaces` | Reader | |
| Throttling | 429 → header `x-ms-ratelimit-microsoft.consumption-retry-after`; 503 → `Retry-After` | | Both headers handled in Polly policy. |

### 2.5 Azure Advisor — api-version `2023-01-01`
`GET /subscriptions/{id}/providers/Microsoft.Advisor/recommendations?$filter=Category eq 'Cost'` — Reader. Phase 4.

### 2.6 Azure Retail Prices — `https://prices.azure.com/api/retail/prices?$filter=...`
Unauthenticated. Azure meters only. Used for "at list price" comparisons.

---

## 3. Partner Center — deferred to Phase 6

`https://api.partnercenter.microsoft.com/v1` · App-only preferred; **App+User requires MFA since 1 April 2026** · **Indirect resellers: no API access** · NCE cancellation/seat reduction only within 7 days of purchase/renewal · mid-term change deletes `scheduledNextTermInstructions` · no API lists EST subscriptions · reconciliation exports are on Graph: `microsoft.graph.partners.billing` (`billedUsage/unbilledUsage/billedReconciliation/unbilledReconciliation: export` → poll → manifest), direct partners only, ~24h latency.

---

## 4. Identity platform

| Item | Value |
|---|---|
| App registration | Multi-tenant, `signInAudience: AzureADMultipleOrgs`; custom publisher domain (not `*.onmicrosoft.com`); privacy + terms URLs set |
| Publisher verification | Required. Microsoft Cloud Partner Program account; verifier holds Application/Cloud Application Administrator in Entra and CPP Partner/Account Admin in Partner Center; MFA. |
| Sign-in flow | OIDC Authorization Code + PKCE (Microsoft.Identity.Web) |
| Admin consent URL | `https://login.microsoftonline.com/organizations/v2.0/adminconsent?client_id=…&scope=…&redirect_uri=…&state=…` |
| App-only for sync | Client credentials with **certificate** from Key Vault; token per tenant `https://login.microsoftonline.com/{tid}/oauth2/v2.0/token`; scope `https://graph.microsoft.com/.default` or `https://management.azure.com/.default` |
| Delegated for user actions | On-Behalf-Of not needed in single-app deployment; use incremental consent + MSAL cache |
| Token cache | Redis, MSAL serialisation, Data Protection keys in Key Vault |
| Revocation signal | `invalid_grant` / `AADSTS65001` / 401 on app-only ⇒ `Tenant.Status = NeedsReconsent` |
