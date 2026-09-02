# 04 — Data Model

Conventions: `UNIQUEIDENTIFIER` keys unless noted; every business table has `TenantId` (FK → Tenant) in its unique key; `RowVersion` on mutable tables; `CreatedUtc`/`UpdatedUtc`; money `decimal(19,6)` for unit/rate, `decimal(19,4)` for totals, always with `Currency char(3)`. Global reference tables have no `TenantId` and are excluded from RLS.

## 1. Tenancy & onboarding

```
Organization                    -- optional roll-up above tenants (enterprise)
  OrganizationId PK, Name, HomeTenantId, Region

Tenant
  TenantId PK                   -- Entra tid (natural key)
  OrganizationId FK null
  DisplayName, DefaultDomain, Region
  Status                        -- Provisioning|Active|NeedsReconsent|GracePeriod|Deleted
  ConsentGrantedUtc, ConsentGrantedByObjectId
  AgreementTypePrimary          -- MOSA|MCA|EA|MPA|CspManaged|Unknown
  IsPartnerManaged, ManagingPartnerTenantId null
  Features (JSON)               -- feature flags: UsageInsights, IdentifiableNames, LifecycleOps
  DeleteScheduledUtc null

TenantCapabilityProfile
  TenantId PK/FK
  GraphLicensing, GraphUsage, ArmAccess, CostManagement, BillingAccount, BillingTransactions, PartnerCenter  -- each: Available|Unavailable
  Reasons (JSON)                -- per capability: reason code + detail
  LastProfiledUtc, NextProfileUtc

OnboardingStep
  OnboardingStepId PK, TenantId FK, Step, Status, StartedUtc, CompletedUtc, Error
  UNIQUE(TenantId, Step)

PendingConsentRequest
  PendingConsentRequestId PK, TenantId, RequestedByObjectId, RequestedByUpn, SentToEmail, Token, ExpiresUtc, CompletedUtc null

AppUser
  AppUserId PK, TenantId FK, EntraObjectId, Upn, DisplayName, Role  -- Owner|Analyst|Viewer|SubscriptionManager
  ScopeSubscriptionIds (JSON) null
  UNIQUE(TenantId, EntraObjectId)
```

## 2. Reference (global)

```
Product          ProductId PK, SkuId, SkuPartNumber, DisplayName, ProductFamily, ServicePlanIds (JSON)   UNIQUE(SkuId)
ServicePlan      ServicePlanId PK, ServicePlanName, DisplayName, AppliesTo
SkuOverlapRule   RuleId PK, PrimarySkuId, RedundantSkuId, Reason
ListPriceReference
  ListPriceReferenceId PK, SkuId, Currency, Region, PricePerSeatMonthly, PricePerSeatAnnual, EffectiveFrom, EffectiveTo, SourceNote
  UNIQUE(SkuId, Currency, Region, EffectiveFrom)
```

## 3. Licensing (Graph)

```
LicenseSku
  LicenseSkuId PK, TenantId FK, SkuId, SkuPartNumber, AppliesTo, CapabilityStatus
  PrepaidEnabled, PrepaidSuspended, PrepaidWarning, PrepaidLockedOut, ConsumedUnits
  SubscriptionIds (JSON), LastSyncedUtc, SyncRunId FK
  UNIQUE(TenantId, SkuId)

DirectoryUser
  UserId PK, TenantId FK, EntraObjectId, Upn, DisplayName, AccountEnabled
  Department, CompanyName, OfficeLocation, UsageLocation, UserType
  LastSyncedUtc
  UNIQUE(TenantId, EntraObjectId)

LicenseAssignment
  LicenseAssignmentId PK, TenantId FK, UserId FK, LicenseSkuId FK
  AssignedByGroupId null, DisabledServicePlanIds (JSON), FirstSeenUtc, LastSeenUtc
  UNIQUE(TenantId, UserId, LicenseSkuId)

GraphDeltaState
  TenantId PK, Resource ('users'), DeltaLink, LastFullSyncUtc
```

## 4. Subscriptions (unified)

```
Subscription
  SubscriptionId PK, TenantId FK
  SourceSystem                  -- Graph|AzureBilling|PartnerCenter
  ExternalId                    -- commerceSubscriptionId | billingSubscription name | PC id
  GraphCompanySubscriptionId null, BillingSubscriptionName null
  SkuId, SkuPartNumber, SkuDescription, ProductTypeId, ProductCategory  -- SeatBased|UsageBased|Software|ReservationOrder|SavingsPlanOrder|Other|null
  Status, IsTrial, TotalLicenses/Quantity
  CreatedDateTime, PurchaseDate, NextLifecycleDateTime
  TermDuration, TermStartDate, TermEndDate, BillingFrequency
  AutoRenewEnabled bit null     -- NULL = unknowable
  AutoRenewSource               -- AzureBilling|PartnerCenter|null
  CancellationAllowedEndDate null, NextBillingCycleDetails (JSON), RenewalTermDetails (JSON)
  OwnerTenantId null, ProvisioningTenantId null
  BillingAccountId FK null, BillingProfileId FK null, InvoiceSectionId FK null
  AzureSubscriptionGuid null    -- for UsageBased
  LastSyncedUtc, SyncRunId FK
  UNIQUE(TenantId, SourceSystem, ExternalId)
  INDEX (TenantId, NextLifecycleDateTime) INCLUDE (SkuPartNumber, Status, TotalLicenses, AutoRenewEnabled) WHERE NextLifecycleDateTime IS NOT NULL
  INDEX (TenantId, TermEndDate) WHERE TermEndDate IS NOT NULL
```

## 5. Billing hierarchy (Microsoft.Billing)

```
BillingAccount     BillingAccountId PK, TenantId FK, Name, DisplayName, AgreementType, AccountType, LastSyncedUtc   UNIQUE(TenantId, Name)
BillingProfile     BillingProfileId PK, TenantId FK, BillingAccountId FK, Name, DisplayName, Currency, InvoiceDay, Status   UNIQUE(TenantId, BillingAccountId, Name)
InvoiceSection     InvoiceSectionId PK, TenantId FK, BillingProfileId FK, Name, DisplayName   UNIQUE(TenantId, BillingProfileId, Name)
AssociatedBillingTenant  Id PK, TenantId FK, BillingAccountId FK, AssociatedTenantId, BillingManagementEnabled, ProvisioningEnabled, GraphConsentStatus, LastSeenUtc
BillingRoleSnapshot      Id PK, TenantId FK, Scope, RoleDefinitionId, RoleName, PrincipalObjectId, ObservedUtc   -- for onboarding explanations

Invoice
  InvoiceId PK, TenantId FK, BillingAccountId FK, BillingProfileId FK null, InvoiceSectionId FK null
  ExternalInvoiceId, InvoiceDate, DueDate, BillingPeriodStart, BillingPeriodEnd
  TotalAmount, Currency, Status, DocumentType, IsCreditNote
  -- NO document URL column (short-lived SAS; fetch on demand)
  UNIQUE(TenantId, BillingAccountId, ExternalInvoiceId)
```

## 6. Costs

```
TransactionFact                  -- Microsoft.Billing transactions (MCA/MPA)
  TransactionFactId PK bigint
  TenantId FK, BillingAccountId FK, BillingProfileId FK null, InvoiceSectionId FK null, InvoiceId FK null
  ExternalTransactionId, Kind (Billed|Unbilled), TransactionType, TransactionDate
  ServicePeriodStart, ServicePeriodEnd
  ProductTypeId, ProductType, ProductFamily, ProductDescription, SkuId null
  Quantity, Units, UnitOfMeasure
  EffectivePrice, MarketPrice, PricingCurrency
  SubTotal, Tax, TransactionAmount, AzureCreditApplied, BillingCurrency
  IsThirdParty, SyncRunId FK
  UNIQUE(TenantId, BillingAccountId, ExternalTransactionId, Kind)
  INDEX (TenantId, ServicePeriodStart DESC) INCLUDE (TransactionAmount, SubTotal, ProductFamily, Kind, InvoiceId)
  INDEX (TenantId, SkuId, ServicePeriodStart DESC) INCLUDE (EffectivePrice, Quantity)

ConsumptionFact                  -- Cost Management (Azure)
  ConsumptionFactId PK bigint
  TenantId FK, UsageDate date
  AzureSubscriptionGuid, SubscriptionName, ResourceGroupName null, ResourceId null, ResourceName null, ResourceType null, ResourceLocation null
  ServiceName, ServiceTier null, MeterCategory, MeterSubCategory null, MeterName null
  Quantity, UnitOfMeasure
  CostAmount, Currency, CostUsd null
  CostMetric (Actual|Amortized), PricingModel, ChargeType, PublisherType, Provider
  Tags (JSON) null, CostCenter null
  SyncRunId FK
  UNIQUE(TenantId, UsageDate, AzureSubscriptionGuid, ISNULL(ResourceId,''), MeterCategory, ISNULL(MeterName,''), CostMetric)
  PARTITION by UsageDate (monthly); CLUSTERED COLUMNSTORE once large
  INDEX (TenantId, UsageDate DESC) INCLUDE (CostAmount, ServiceName, AzureSubscriptionGuid, ResourceGroupName, CostMetric)
  INDEX (TenantId, AzureSubscriptionGuid, ResourceGroupName, UsageDate DESC) INCLUDE (CostAmount, ResourceId)

CostMonthlyRollup                -- pre-aggregated for dashboards
  TenantId, YearMonth, Source (Consumption|Transaction), Dimension, DimensionValue, Currency, Amount, CostMetric
  UNIQUE(TenantId, YearMonth, Source, Dimension, DimensionValue, Currency, CostMetric)

AzureSubscription
  AzureSubscriptionId PK, TenantId FK, SubscriptionGuid, DisplayName, State, AgreementType, BillingProfileId FK null, InvoiceSectionId FK null, CostCenter null, RbacProbeStatus, LastSyncedUtc
  UNIQUE(TenantId, SubscriptionGuid)

DerivedUnitPrice
  DerivedUnitPriceId PK, TenantId FK, SkuId, ProductTypeId null
  ServicePeriodStart, ServicePeriodEnd null
  UnitPrice, Currency, BillingFrequency null
  Source (BillingTransaction|PartnerShared|Manual|ListPriceReference)
  SourceTransactionFactId FK null, EnteredByAppUserId FK null, DerivedUtc
  UNIQUE(TenantId, SkuId, ServicePeriodStart, Source)

Reservation / SavingsPlan / AdvisorRecommendation / CostAlert  -- Phase 4; straightforward mirrors of API shapes with TenantId
```

## 7. Usage

```
UsageReportRun      Id PK, TenantId FK, Report, Period, ReportDate, AnonymisedNames bit, SyncRunId FK   UNIQUE(TenantId, Report, ReportDate)
UserActivity        Id PK bigint, TenantId FK, UsageReportRunId FK, UserKey (Upn or hash), UserId FK null, Workload, LastActivityDate null, Metrics (JSON), IsLicensed
                    INDEX (TenantId, Workload, LastActivityDate)
```

`UserId` is null when anonymised. Department-level views join only when `UserId` is populated.

## 8. History

```
LicenseSkuSnapshot       TenantId, SnapshotDate, SkuId, PrepaidEnabled, ConsumedUnits, AssignedCount, DisabledAccountCount, InactiveCount   UNIQUE(TenantId, SnapshotDate, SkuId)
SubscriptionSnapshot     TenantId, SnapshotDate, SubscriptionId, Status, Quantity, AutoRenewEnabled, TermEndDate                               UNIQUE(TenantId, SnapshotDate, SubscriptionId)
```

Retention: ConsumptionFact hot 24 months then partition-switch to archive; rollups and snapshots indefinite.

## 9. Operations

```
SyncRun      SyncRunId PK, TenantId FK, JobType, Status, StartedUtc, CompletedUtc, RecordsProcessed, ErrorCode, ErrorMessage, CorrelationId, ContinuationToken null, ValidationNotes
             INDEX (TenantId, JobType, StartedUtc DESC)
staging_*    one per synced entity; columns mirror live + SyncRunId; truncated after MERGE
AuditLog     AuditLogId PK bigint, TenantId FK, ActorObjectId, ActorUpn, Action, EntityType, EntityId, OldValue (JSON), NewValue (JSON), OccurredUtc, SourceIp, CorrelationId, Outcome
             append-only; INDEX (TenantId, OccurredUtc DESC)
```

## 10. Row-Level Security

```sql
CREATE FUNCTION dbo.fn_TenantPredicate(@TenantId UNIQUEIDENTIFIER)
RETURNS TABLE WITH SCHEMABINDING AS
RETURN SELECT 1 AS ok WHERE @TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS UNIQUEIDENTIFIER)
   OR CAST(SESSION_CONTEXT(N'IsSystem') AS BIT) = 1;

CREATE SECURITY POLICY dbo.TenantPolicy
  ADD FILTER PREDICATE dbo.fn_TenantPredicate(TenantId) ON dbo.LicenseSku,
  ADD BLOCK  PREDICATE dbo.fn_TenantPredicate(TenantId) ON dbo.LicenseSku,
  -- repeat for every tenant-scoped table (generated by migration helper)
WITH (STATE = ON);
```

`IsSystem` is set only by the Sync worker's connection interceptor for cross-tenant scheduler queries; the Web app never sets it.
