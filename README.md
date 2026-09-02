# MLCP — Microsoft License & Cost Management Platform

Public multi-tenant SaaS: any organization holding Microsoft licences signs in with Entra ID and tracks licences, usage, subscriptions, Azure costs, and — where the agreement allows — prices, invoices, and renewals.

Start with [`CLAUDE.md`](CLAUDE.md), then [`docs/00-README.md`](docs/00-README.md).

**Current phase:** Phase 0 — foundation and trust. See [`docs/05-implementation-plan.md`](docs/05-implementation-plan.md).

## Quick start (local)

```bash
docker compose up -d                      # SQL Server, Redis, Azurite, Service Bus emulator
dotnet tool restore
dotnet restore && dotnet build && dotnet test
```

```bash
export MLCP_MIGRATIONS_CONNECTION="Server=localhost,1433;Database=Mlcp;User Id=sa;Password=Local_Dev_Password_1;TrustServerCertificate=True;Encrypt=True"
dotnet dotnet-ef database update --project src/Mlcp.Persistence --startup-project src/Mlcp.Web

dotnet user-secrets set "ConnectionStrings:MlcpDatabase" "$MLCP_MIGRATIONS_CONNECTION" -p src/Mlcp.Web
dotnet user-secrets set "ConnectionStrings:Redis" "localhost:6379" -p src/Mlcp.Web
dotnet user-secrets set "AzureAd:ClientId" "<client id>" -p src/Mlcp.Web
dotnet user-secrets set "AzureAd:ClientSecret" "<dev secret>" -p src/Mlcp.Web

dotnet run --project src/Mlcp.Web     # web
dotnet run --project src/Mlcp.Sync    # worker
```

Sign-in needs an Entra app registration — follow [`infra/entra.md`](infra/entra.md). Without email configured, consent request links are written to the log so the whole onboarding flow still works locally.

## Deploying to Azure

[`infra/deploy.md`](infra/deploy.md) is the runbook. Summary:

```bash
az deployment group create -g <rg> --template-file infra/main.bicep \
  --parameters environmentName=prod entraClientId=<id> \
               sqlAdminGroupObjectId=<group> sqlAdminGroupName='MLCP SQL Admins' \
               containerRegistryServer=<acr>.azurecr.io \
               webImage=<acr>.azurecr.io/mlcp-web:<tag> \
               syncImage=<acr>.azurecr.io/mlcp-sync:<tag>
```

One stack per region — a tenant's region is chosen at connection and never migrated silently.

| Piece | Choice | Why |
|---|---|---|
| Compute | Container Apps: web with ingress, worker without | The worker has cross-tenant database access and no reason to be reachable |
| Database | Azure SQL serverless, **Entra-only auth** | No SQL login exists, so there is no database password to leak or rotate |
| Secrets | Key Vault + user-assigned managed identity | The app holds no credential capable of fetching its own credential |
| Redis | Connection string stored in Key Vault, resolved at runtime | An inline Container Apps secret is recorded in ARM deployment history |
| Networking | Private endpoints for SQL, Redis, Service Bus, Key Vault, Storage; public access disabled | A leaked connection string is useless from outside the VNet |
| Data Protection | Blob storage + Key Vault key | Keys back auth cookies, the MSAL cache and consent state, so they outlive any instance |
| Queue | Service Bus Premium, sessions per tenant, duplicate detection | Ordering within a tenant; a restarted scheduler cannot double-run a sync |

Customers grant Azure and billing roles to the deployment's `workloadPrincipalId` in **their own** tenants. Admin consent grants neither — that is why the checklist shows all three authorisation systems separately.

## Tenant isolation

Four layers, all shipped together and all tested ([ADR-003](docs/06-decisions/ADR-003-tenant-isolation-four-layers.md)):

| # | Layer | Where |
|---|---|---|
| 1 | `TenantId` on every business table, part of every unique key | [`TenantEntity.cs`](src/Mlcp.Domain/Common/TenantEntity.cs) |
| 2 | EF global query filter, applied to every `ITenantScoped` entity by convention | [`MlcpDbContext.cs`](src/Mlcp.Persistence/MlcpDbContext.cs) |
| 3 | SQL Server row-level security on `SESSION_CONTEXT`, stamped read-only | [`TenantRlsScript.cs`](src/Mlcp.Persistence/Rls/TenantRlsScript.cs), [`TenantSessionInterceptor.cs`](src/Mlcp.Persistence/Interceptors/TenantSessionInterceptor.cs) |
| 4 | API boundary returns **404**, never 403 | [`TenantScopedAttribute.cs`](src/Mlcp.Web/Infrastructure/TenantScopedAttribute.cs) |

```bash
dotnet test --filter Category=TenantIsolation
```

The database half needs Docker and **skips without it**. CI sets `MLCP_REQUIRE_DOCKER=true`, turning the skip into a failure, so a runner whose container service did not start reports a broken build rather than a green one in which nothing ran.

Adding a tenant-scoped entity means adding its table to `TenantRlsScript.TenantScopedTables` and writing a migration. `RlsCoverageTests` compares that list against the EF model on every build and needs no database, so drift fails everywhere.

There is exactly one cross-tenant door, [`ISystemDbContextFactory`](src/Mlcp.Persistence/SystemDbContextFactory.cs). It is a separate type with its own options and its own interceptor, registered only in the sync worker, so every use shows up in a constructor signature.

## Layout

```
src/
  Mlcp.Domain/          Entities, value objects, domain rules. No dependencies.
  Mlcp.Shared/          Tenant context, redaction, Polly resilience, token acquisition.
  Mlcp.Application/     Use cases, capability gates, sync pipeline. No EF, no HTTP.
  Mlcp.Persistence/     DbContext, configurations, migrations, RLS, stores.
  Mlcp.Integration.*/   Graph, Azure, Partner providers.
  Mlcp.Web/             MVC + Razor. Thin controllers.
  Mlcp.Sync/            Worker: scheduler, Service Bus dispatch, deletion sweep.
tests/
  Mlcp.UnitTests/       No I/O. Runs everywhere.
  Mlcp.IntegrationTests/  Testcontainers SQL Server. TenantIsolation category.
docs/                   Authoritative specs. If code and docs disagree, raise it.
infra/                  Bicep, deploy runbook, Entra runbook, local emulator config.
```

`Application` must never reference `Persistence` or `Integration.*`; wiring happens in `Web` and `Sync`.

## Deviations from CLAUDE.md

Recorded as ADRs rather than left as surprises:

- **[ADR-011](docs/06-decisions/ADR-011-net8-target-framework.md)** — targets `net8.0`, not the `net9.0` CLAUDE.md fixes. The .NET 9 SDK is not available on the build machine.
- **[ADR-012](docs/06-decisions/ADR-012-fluentassertions-version-pin.md)** — FluentAssertions pinned to 7.x; version 8 requires a paid commercial licence.
- **[ADR-013](docs/06-decisions/ADR-013-sync-load-modes.md)** — the validation gate applies row-count checks to full loads only. CLAUDE.md rule 6 as written would fail almost every delta sync.

## Build policy

`TreatWarningsAsErrors` and `EnforceCodeStyleInBuild` are on solution-wide, NuGet audit failures are errors, and the DI container is validated at startup in every environment. Package versions are centralised in `Directory.Packages.props`; never add a version to a `.csproj`.
