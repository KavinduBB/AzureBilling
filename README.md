# MLCP — Microsoft License & Cost Management Platform

MLCP is a public, multi-tenant SaaS. Any organisation that holds Microsoft licences can sign in with
Entra ID and track licences, usage, subscriptions and Azure costs. Where the agreement allows, it
also tracks prices, invoices and renewals.

Start with [`CLAUDE.md`](CLAUDE.md), then [`docs/00-README.md`](docs/00-README.md), which includes
the ADR index.

**Current phase:** Phase 0, foundation and trust. See
[`docs/05-implementation-plan.md`](docs/05-implementation-plan.md).

## Prerequisites

- **.NET 10 SDK.** [`global.json`](global.json) pins `10.0.100` with `rollForward: latestFeature`,
  so any 10.0.x feature band works ([ADR-014](docs/06-decisions/ADR-014-net10-target-framework.md)).
  Check with `dotnet --version` from the repository root.
- **Docker**, for `docker compose` and the Testcontainers-based integration tests. Without Docker,
  point the tests at a disposable SQL Server with `MLCP_TEST_SQL` (below).
- **Local tools:** run `dotnet tool restore`. It installs `dotnet-ef` from
  [`.config/dotnet-tools.json`](.config/dotnet-tools.json).
- **For sign-in:** a *development* Entra app registration ([`infra/entra.md`](infra/entra.md) §5).

## Local setup

### 1. Dependencies

```bash
docker compose up -d        # SQL Server, Redis, Service Bus emulator (uses the same SQL Server)
dotnet tool restore
dotnet build
dotnet test                 # the database tests need Docker; see "Tests"
```

### 2. Database

`dotnet ef` builds the model through `MlcpDbContextFactory`, which reads
`MLCP_MIGRATIONS_CONNECTION`. Without it, the factory uses the local compose defaults.

```bash
export MLCP_MIGRATIONS_CONNECTION="Server=localhost,1433;Database=Mlcp;User Id=sa;Password=Local_Dev_Password_1;TrustServerCertificate=True;Encrypt=True"
dotnet dotnet-ef database update -p src/Mlcp.Persistence -s src/Mlcp.Web

# After changing the model:
dotnet dotnet-ef migrations add <YYYYMMDDHHMM_Description> -p src/Mlcp.Persistence -s src/Mlcp.Web
dotnet dotnet-ef migrations has-pending-model-changes -p src/Mlcp.Persistence -s src/Mlcp.Web
```

Locally, both hosts connect as `sa`. `sa` is `db_owner`, so the `db_owner` branch of the RLS system
clause applies to both. The web/worker role separation is exercised only by the isolation suite and
in Azure.

### 3. Secrets (user-secrets, never appsettings)

Both hosts use `dotnet user-secrets`. Nothing below belongs in `appsettings*.json` (CLAUDE.md
rule 4).

**Web** (`src/Mlcp.Web`):

```bash
DB="Server=localhost,1433;Database=Mlcp;User Id=sa;Password=Local_Dev_Password_1;TrustServerCertificate=True;Encrypt=True"
dotnet user-secrets set "ConnectionStrings:MlcpDatabase" "$DB" -p src/Mlcp.Web
dotnet user-secrets set "ConnectionStrings:Redis" "localhost:6379" -p src/Mlcp.Web
dotnet user-secrets set "ConnectionStrings:ServiceBus" "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;" -p src/Mlcp.Web
dotnet user-secrets set "AzureAd:ClientId" "<dev core registration client id>" -p src/Mlcp.Web
dotnet user-secrets set "AzureAd:ClientSecret" "<dev-only client secret>" -p src/Mlcp.Web
dotnet user-secrets set "AzureAd:UsageInsightsClientId" "<dev usage insights client id>" -p src/Mlcp.Web
```

**Sync worker** (`src/Mlcp.Sync`):

```bash
dotnet user-secrets set "ConnectionStrings:MlcpDatabase" "$DB" -p src/Mlcp.Sync
dotnet user-secrets set "ConnectionStrings:ServiceBus" "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;" -p src/Mlcp.Sync
dotnet user-secrets set "AzureAd:ClientId" "<dev core registration client id>" -p src/Mlcp.Sync
dotnet user-secrets set "AzureAd:ClientSecret" "<dev-only client secret>" -p src/Mlcp.Sync
dotnet user-secrets set "AzureAd:UsageInsightsClientId" "<dev usage insights client id>" -p src/Mlcp.Sync
```

**Notes on these values:**
- **The client secret is for development only.** It belongs to the development registrations; no
  deployed environment has one.
- **Service Bus:** the emulator has no Entra support, so locally both hosts use this connection
  string. In Azure they use `Mlcp:ServiceBus:FullyQualifiedNamespace` with their managed identity.
- **Redis:** leave `Mlcp:Redis:UseEntraAuth` unset locally. It is `true` only for Azure Managed
  Redis.
- **Development defaults:**
  - The tenant-region directory is in memory, with a single region.
  - Data Protection keys use the default on-disk key store.
  - Without email settings, consent-request links are written to the log.

### 4. Run

```bash
dotnet run --project src/Mlcp.Web --launch-profile https   # https://localhost:7003
dotnet run --project src/Mlcp.Sync
```

On the development registration, register the redirect URIs
`https://localhost:7003/signin-oidc` and `https://localhost:7003/onboarding/consent-callback`, plus
`https://localhost:7003/onboarding/usage-insights-callback` for the Usage Insights development
registration. Use the `https` profile: the redirect URIs above are HTTPS.

### Tests

```bash
dotnet test --filter Category=TenantIsolation
```

- **Docker.** The database half of the suite needs Docker and **skips without it**. CI sets
  `MLCP_REQUIRE_DOCKER=true`, which turns the skip into a failure, and asserts a minimum number of
  passing isolation tests.
- **No Docker.** Set `MLCP_TEST_SQL` to a connection string for a **disposable** SQL Server. The
  tests create and drop their own databases on it.
- **New tenant-scoped tables.** Add the table to `TenantRlsScript.TenantScopedTables` and write a
  migration. `RlsCoverageTests` compares that list with the EF model on every build.

## Deploying to Azure

[`infra/deploy.md`](infra/deploy.md) is the runbook, and [`infra/entra.md`](infra/entra.md) covers
the two app registrations. Routine deployments run through
[`.github/workflows/deploy.yml`](.github/workflows/deploy.yml), from `main`, with approval:

```
build & push (web, sync, migrate) → what-if → approval → deploy (new web revision at 0%)
  → migrate job (in the VNet) → smoke /health/ready → traffic 100% → worker image → deactivate old revisions
```

| Piece | Choice | Why |
|---|---|---|
| Topology | `infra/global.bicep` (Front Door, `TenantRegions` directory) plus one `infra/main.bicep` stack per region | Customer data stays in its region; only `tid → region` is global (ADR-021) |
| Compute | Container Apps. Web has ingress and multiple revisions; the worker has no ingress; migrations run as jobs | The worker has cross-tenant database access and no reason to be reachable; the schema leads the code |
| Identities | Separate managed identities for web, worker and migrator, each with its own database user | The web tier cannot use the RLS system bypass (ADR-026) |
| Database | Azure SQL serverless, **Entra-only auth** | No SQL login exists, so there is no password to leak or rotate |
| Secrets | Key Vault. One certificate credential serves both app registrations | No client secret in any deployed environment |
| Cache | **Azure Managed Redis**, Entra auth, access keys disabled | No Redis key exists |
| Networking | Private endpoints for SQL, Redis, Service Bus, Key Vault and storage; public access disabled | A leaked connection string is useless from outside the VNet |
| Queue | Service Bus Premium: sessions per tenant, duplicate detection; web sends, the worker receives | Ordering within a tenant; least privilege |

Customers grant Azure and billing roles to the **MLCP enterprise application (service principal)**
in **their own** tenant ([Guide B/C](docs/07-onboarding-guides.md)). They never grant roles to
MLCP's managed identities. Admin consent grants neither kind of role, which is why the checklist
shows each authorisation system separately.

## Tenant isolation

Four layers, all shipped together and all tested
([ADR-003](docs/06-decisions/ADR-003-tenant-isolation-four-layers.md)):

| # | Layer | Where |
|---|---|---|
| 1 | `TenantId` on every business table, part of every unique key | [`TenantEntity.cs`](src/Mlcp.Domain/Common/TenantEntity.cs) |
| 2 | EF global query filter, applied to every `ITenantScoped` entity by convention | [`MlcpDbContext.cs`](src/Mlcp.Persistence/MlcpDbContext.cs) |
| 3 | SQL Server row-level security (`TenantSecurityPolicy`) on `SESSION_CONTEXT`, stamped read-only. The system bypass also requires the `mlcp_system` role | [`TenantRlsScript.cs`](src/Mlcp.Persistence/Rls/TenantRlsScript.cs), [`TenantSessionInterceptor.cs`](src/Mlcp.Persistence/Interceptors/TenantSessionInterceptor.cs) |
| 4 | The API boundary returns **404**, never 403 | [`TenantScopedAttribute.cs`](src/Mlcp.Web/Infrastructure/TenantScopedAttribute.cs) |

There is exactly one cross-tenant door,
[`ISystemDbContextFactory`](src/Mlcp.Persistence/SystemDbContextFactory.cs):
- It is registered only in the sync worker.
- Its bypass works only for a database user in `mlcp_system`.

## Layout

```
src/
  Mlcp.Domain/          Entities, value objects, domain rules. No dependencies.
  Mlcp.Shared/          Tenant context, redaction, Polly resilience, failure classification, token acquisition.
  Mlcp.Application/     Use cases, capability gates, sync pipeline. No EF, no HTTP.
  Mlcp.Persistence/     DbContext, configurations, migrations, RLS, stores; Dockerfile.migrate.
  Mlcp.Integration.*/   Graph, Azure, Partner providers.
  Mlcp.Web/             MVC + Razor. Thin controllers.
  Mlcp.Sync/            Worker: scheduler, Service Bus dispatch, deletion sweep.
tests/
  Mlcp.UnitTests/         No I/O. Runs everywhere.
  Mlcp.IntegrationTests/  Testcontainers SQL Server (or MLCP_TEST_SQL). TenantIsolation category.
docs/                   Authoritative specs. If code and docs disagree, raise it.
infra/                  Bicep (global + regional), SQL scripts, deploy and Entra runbooks, local emulator config.
.github/                CI and deployment workflows, deployment scripts.
```

`Application` must never reference `Persistence` or `Integration.*`. Wiring happens in `Web` and
`Sync`.

## Deviations from the original brief

These are recorded as ADRs rather than left as surprises (see the full index in
[`docs/00-README.md`](docs/00-README.md)):

- **[ADR-014](docs/06-decisions/ADR-014-net10-target-framework.md):** targets .NET 10 LTS. It
  supersedes ADR-011 (net8.0).
- **[ADR-012](docs/06-decisions/ADR-012-fluentassertions-version-pin.md):** FluentAssertions is
  pinned to 7.x, because version 8 requires a paid commercial licence.
- **[ADR-013](docs/06-decisions/ADR-013-sync-load-modes.md) /
  [ADR-024](docs/06-decisions/ADR-024-validation-gate-per-run-and-period.md):** the validation
  gate's volume checks apply to full loads, per load mode and period, with an audited override.
- **[ADR-026](docs/06-decisions/ADR-026-deployment-identities-and-migrations.md):** the worker is a
  long-running Container App. Container Apps Jobs are used for migrations.

## Build policy

- **Warnings and audits.** `TreatWarningsAsErrors` and `EnforceCodeStyleInBuild` are on
  solution-wide, and NuGet audit failures are errors. CI also fails on any vulnerable package,
  direct or transitive.
- **Startup validation.** The DI container is validated at startup in every environment.
- **Package versions.** They are centralised in `Directory.Packages.props`. Never add a version to
  a `.csproj`.
