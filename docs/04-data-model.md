# 04 — Data Model

Conventions: `UNIQUEIDENTIFIER` keys unless noted; every business table has `TenantId` (FK → Tenant) in its unique key; `RowVersion` on mutable tables; `CreatedUtc`/`UpdatedUtc`; money `decimal(19,6)` for unit/rate, `decimal(19,4)` for totals, always with `Currency char(3)`. Global reference tables have no `TenantId` and are excluded from RLS.

## 1. Tenancy & onboarding

```
Organization                    -- optional roll-up above tenants (enterprise)
  OrganizationId PK, Name, HomeTenantId, Region, CreatedUtc

OrganizationViewGrant           -- GLOBAL (no RLS); roll-up grants; Phase 3 (P3-8, ADR-022)
  OrganizationViewGrantId PK
  ViewerTenantId, TargetTenantId
  GrantedByObjectId             -- a verified admin of the TARGET tenant (ADR-018)
  RequestedByObjectId, RequestedUtc, GrantedUtc, RevokedUtc null, RevokedByObjectId null
  UNIQUE(ViewerTenantId, TargetTenantId) WHERE RevokedUtc IS NULL
  INDEX (ViewerTenantId, TargetTenantId) WHERE RevokedUtc IS NULL   -- used by the RLS predicate

Tenant
  TenantId PK                   -- Entra tid (natural key)
  OrganizationId FK null
  DisplayName, DefaultDomain null
  Region                        -- region code (eu|us); claimed in the global directory at connect (ADR-021)
  Status                        -- NotConnected|ConsentPendingVerification|Provisioning|Active|NeedsReconsent|GracePeriod
                                --   Deleted is transient: the row is removed, and DeletionCertificate is the record
  ConsentCallbackUtc null       -- last admin-consent callback; starts the propagation window (ADR-016, ADR-018)
  ConsentGrantedUtc null, ConsentGrantedByObjectId null   -- set only after app-only verification succeeds
  AgreementTypePrimary          -- NotDiscovered|Mosa|Mca|Ea|Mpa|CspManaged|Unknown|Undetermined (ADR-020)
  IsPartnerManaged, ManagingPartnerTenantId null
  Features (JSON)               -- flags: UsageInsights, IdentifiableNames, LifecycleOps
  NeedsReconsentReason null     -- the classified failure (ADR-016), e.g. GrantRevoked:AADSTS7000112
  NeedsReconsentSinceUtc null, NextReconsentProbeUtc null, ReconsentProbeAttempts
  DisconnectedUtc null, DeleteScheduledUtc null
  CreatedUtc, UpdatedUtc, RowVersion

TenantCapabilityProfile         -- one row per tenant, as stored
  TenantId PK/FK
  Statuses (JSON)               -- per capability (GraphLicensing, GraphUsage, ArmAccess, CostManagement,
                                --   BillingAccount, BillingTransactions, PartnerCenter):
                                --   { IsAvailable, Reason (CapabilityUnavailableReason) null, Guide,
                                --     Detail null, CheckedUtc, IsStale }
                                --   IsStale: the verdict was kept through a Transient failure
  VerifiedDomains (JSON)        -- from GET /organization; limits "Ask my admin" recipients (ADR-018)
  CostScopeRung null            -- 1 billing profile/account | 2 root management group | 3 per subscription (ADR-017)
  CostScopeDetail (JSON) null   -- the chosen scope ids; per-subscription agreement types
  LastProfiledUtc, NextProfileUtc
  CreatedUtc, UpdatedUtc, RowVersion

OnboardingStep
  OnboardingStepId PK, TenantId FK, Step, Status, StartedUtc, CompletedUtc, Error
  UNIQUE(TenantId, Step)

PendingConsentRequest
  PendingConsentRequestId PK, TenantId FK
  RequestedByObjectId, RequestedByUpn
  SentToEmail                   -- must be in a verified domain
  Token, ExpiresUtc, CompletedUtc null
  SendCount, LastSentUtc        -- rate limits and cooldown (ADR-018)

AppUser
  AppUserId PK, TenantId FK, EntraObjectId, Upn, DisplayName
  Role                          -- Owner|Analyst|Viewer. Owner is DERIVED from the wids claim at every sign-in
                                --   and stored only for display and audit (ADR-018)
  LastSeenUtc null
  ScopeSubscriptionIds (JSON) null                    -- Phase 4 (P4-9)
  UNIQUE(TenantId, EntraObjectId)
  -- No SubscriptionManager column or role value: it is an Entra app role, read from the roles claim (ADR-023)

DeletionCertificate             -- GLOBAL (no RLS); outlives the tenant (ADR-019)
  DeletionCertificateId PK, TenantId (no FK), TenantDisplayName
  DisconnectedUtc, DeletedUtc
  RowsDeleted
  RowCountsByTable (JSON)       -- includes the AuditLog count
  AuditLogSha256                -- SHA-256 of the canonicalised audit log, taken just before the purge
  CorrelationId
  -- written in the SAME transaction as the deletes
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
SyncRun
  SyncRunId PK, TenantId FK, JobType
  Status                        -- Running|Succeeded|Failed|Skipped|Abandoned|Superseded
  LoadMode                      -- Full|Incremental|Append; chosen per run (ADR-024)
  PeriodKey null                -- e.g. 2026-09, or the billing profile's current invoice period
  StartedUtc, CompletedUtc null
  StagedRowCount                -- the full staged count; the ONLY validation baseline
  RecordsProcessed              -- rows affected by MERGE (telemetry only)
  ContinuationToken null        -- checkpointed after each staged page
  SupersededBySyncRunId null    -- set when a later run adopts this run's staging
  ErrorCode null, ErrorMessage null, ValidationNotes null, CorrelationId
  INDEX (TenantId, JobType, StartedUtc DESC)
  INDEX (TenantId, JobType, LoadMode, PeriodKey, Status, CompletedUtc DESC)   -- baseline lookup

SyncGateOverride                -- operator escape hatch for a genuine large drop (ADR-024)
  SyncGateOverrideId PK, TenantId FK, JobType
  Reason, ApprovedBy, CreatedUtc
  ExpiresUtc                    -- 24 h after creation
  ConsumedBySyncRunId null, ConsumedUtc null        -- one-shot

staging_*    one per synced entity; columns mirror live + SyncRunId.
             Cleared on success or on a gate rejection. Kept for resumable failures and swept after 24 h.

AuditLog     -- immutable; two rows per action that has an outbound call (ADR-019)
  AuditLogId PK bigint, TenantId FK (NO cascade)
  ActorObjectId null, ActorUpn null, Action, EntityType, EntityId null
  OldValue (JSON) null, NewValue (JSON) null
  Outcome                       -- Pending (the Attempt row, written BEFORE the call) | Succeeded | Failed | Refused
  AttemptAuditLogId null        -- on an Outcome row: its Attempt row
  FinancialImpactAmount decimal null, FinancialImpactCurrency char(3) null
  OccurredUtc, SourceIp null, CorrelationId
  INDEX (TenantId, OccurredUtc DESC)
  -- There is no UPDATE path: mlcp_web is denied UPDATE and DELETE; rows are deleted only by tenant deletion (mlcp_worker).
  -- AuditQueries.WithOutcome() joins Attempt → Outcome.
```

## 10. Row-Level Security

Implemented in `Mlcp.Persistence/Rls/TenantRlsScript.cs` and created by a migration.
- The predicate applies to every tenant-scoped table.
- `RlsCoverageTests` fails the build when a table is missing from the policy.

```sql
CREATE FUNCTION dbo.fn_TenantPredicate(@TenantId uniqueidentifier)
RETURNS TABLE WITH SCHEMABINDING AS
RETURN SELECT 1 AS fn_TenantPredicateResult
WHERE @TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier)
   -- System bypass (ADR-026, as amended). The flag alone is not enough: the caller must also be
   -- in mlcp_system (or db_owner, for the migrator and operators).
   OR (CAST(SESSION_CONTEXT(N'IsSystem') AS bit) = 1
       AND (IS_MEMBER(N'mlcp_system') = 1 OR IS_MEMBER(N'db_owner') = 1));
   -- Phase 3 (ADR-022) adds this, to the FILTER predicate only:
   -- OR (CAST(SESSION_CONTEXT(N'OrgView') AS bit) = 1 AND EXISTS (
   --       SELECT 1 FROM dbo.OrganizationViewGrant g
   --       WHERE g.ViewerTenantId = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier)
   --         AND g.TargetTenantId = @TenantId AND g.RevokedUtc IS NULL))

CREATE SECURITY POLICY dbo.TenantSecurityPolicy
  ADD FILTER PREDICATE dbo.fn_TenantPredicate(TenantId) ON dbo.LicenseSku,
  ADD BLOCK  PREDICATE dbo.fn_TenantPredicate(TenantId) ON dbo.LicenseSku,
  -- repeated for every tenant-scoped table (TenantRlsScript.TenantScopedTables)
WITH (STATE = ON);
```

- **Session keys.** The interceptors set `SESSION_CONTEXT` keys **read-only** when a connection
  opens:
  - `TenantId`: web and worker per-tenant contexts;
  - `IsSystem`: only the worker's `ISystemDbContextFactory`.
- **Unstamped connections** see nothing, because the comparison yields NULL.
- **Roll-up is read-only.** The BLOCK predicate never admits roll-up access (ADR-022).
- **Global tables have no predicate.** `Product`, `ServicePlan`, `SkuOverlapRule`,
  `ListPriceReference`, `OrganizationViewGrant` and `DeletionCertificate` have no `TenantId`.

## 11. Database principals

- **Roles** are created by migrations.
- **Users** are created by `infra/sql/create-users.sql`.
- Both run as Container Apps jobs (ADR-026).
- The workload users are contained users mapped to the managed identities' client ids.

| Principal | Type | Members / mapping | Permissions |
|---|---|---|---|
| `mlcp_web` | Role | `mlcp_web_user` | DML on the tenant tables it writes. On `AuditLog`: SELECT/INSERT, and **DENY UPDATE, DELETE**. No DDL |
| `mlcp_worker` | Role | `mlcp_worker_user` | DML on sync and staging tables. On `AuditLog`: SELECT/INSERT/**DELETE** (tenant deletion only). `sp_getapplock`. No DDL |
| `mlcp_system` | Role | `mlcp_worker_user` only | No object permissions of its own. Membership is what the RLS system clause checks |
| `mlcp_web_user` | Contained user (`TYPE = E`, SID = web identity client id) | Web identity | Through `mlcp_web` |
| `mlcp_worker_user` | Contained user (`TYPE = E`, SID = worker identity client id) | Worker identity | Through `mlcp_worker` and `mlcp_system` |
| SQL admin Entra group | Server Entra admin | Migrator identity, operators | DDL (migrations), user creation |

**Guards.**
- The web user must never be a member of `mlcp_system`; the create-users job fails if it is.
- An integration test impersonates `mlcp_web` (`EXECUTE AS USER`) and asserts two things:
  - UPDATE and DELETE on `AuditLog` fail;
  - setting `IsSystem` exposes no other tenant's rows.
