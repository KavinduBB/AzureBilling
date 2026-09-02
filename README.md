# MLCP — Microsoft License & Cost Management Platform

Public multi-tenant SaaS: any organization holding Microsoft licences signs in with Entra ID and tracks licences, usage, subscriptions, Azure costs, and — where the agreement allows — prices, invoices, and renewals.

Start with [`CLAUDE.md`](CLAUDE.md), then [`docs/00-README.md`](docs/00-README.md).

**Current phase:** Phase 0 — foundation and trust. See [`docs/05-implementation-plan.md`](docs/05-implementation-plan.md).

## Quick start

```bash
docker compose up -d                      # SQL Server, Redis, Azurite, Service Bus emulator
dotnet tool restore                       # dotnet-ef
dotnet restore
dotnet build
dotnet test
```

Point the app at the local database and apply migrations:

```bash
export MLCP_MIGRATIONS_CONNECTION="Server=localhost,1433;Database=Mlcp;User Id=sa;Password=Local_Dev_Password_1;TrustServerCertificate=True;Encrypt=True"
dotnet dotnet-ef database update --project src/Mlcp.Persistence --startup-project src/Mlcp.Web

dotnet user-secrets set "ConnectionStrings:MlcpDatabase" "$MLCP_MIGRATIONS_CONNECTION" -p src/Mlcp.Web
dotnet user-secrets set "ConnectionStrings:Redis" "localhost:6379" -p src/Mlcp.Web
dotnet user-secrets set "AzureAd:ClientId" "<app registration client id>" -p src/Mlcp.Web

dotnet run --project src/Mlcp.Web
```

Sign-in needs an Entra app registration; follow [`infra/entra.md`](infra/entra.md). Without one the host still starts, so migrations and `/health` work, but every authenticated path will challenge and fail.

## Tenant isolation

Four layers, all shipped together and all tested ([ADR-003](docs/06-decisions/ADR-003-tenant-isolation-four-layers.md)):

| # | Layer | Where |
|---|---|---|
| 1 | `TenantId` on every business table, part of every unique key | [`Mlcp.Domain/Common/TenantEntity.cs`](src/Mlcp.Domain/Common/TenantEntity.cs) |
| 2 | EF global query filter, applied to every `ITenantScoped` entity by convention | [`MlcpDbContext.cs`](src/Mlcp.Persistence/MlcpDbContext.cs) |
| 3 | SQL Server row-level security on `SESSION_CONTEXT` | [`TenantRlsScript.cs`](src/Mlcp.Persistence/Rls/TenantRlsScript.cs), [`TenantSessionInterceptor.cs`](src/Mlcp.Persistence/Interceptors/TenantSessionInterceptor.cs) |
| 4 | API boundary returns **404**, never 403 | [`TenantScopedAttribute.cs`](src/Mlcp.Web/Infrastructure/TenantScopedAttribute.cs) |

Run the suite before any PR touching `Persistence`, `Application` or `Web`:

```bash
dotnet test --filter Category=TenantIsolation
```

The database half of that suite needs Docker and **skips without it**. CI sets `MLCP_REQUIRE_DOCKER=true`, which turns the skip into a failure, so a pipeline whose container runtime did not start reports a broken build rather than a green one in which nothing ran.

Adding a tenant-scoped entity means adding its table to `TenantRlsScript.TenantScopedTables` and writing a migration. `RlsCoverageTests` compares that list against the EF model on every build and fails if they diverge — it needs no database, so it runs everywhere.

## Layout

```
src/
  Mlcp.Domain/          Entities, value objects, domain rules. No dependencies.
  Mlcp.Shared/          Tenant context, redaction, Polly resilience. No project dependencies.
  Mlcp.Application/     Use cases, capability gates, sync pipeline. No EF, no HTTP.
  Mlcp.Persistence/     DbContext, configurations, migrations, RLS.
  Mlcp.Integration.*/   Graph, Azure, Partner providers.
  Mlcp.Web/             MVC + Razor + /api/v1. Thin controllers.
  Mlcp.Sync/            Worker host: scheduler, Service Bus consumers, SyncRun pipeline.
tests/
  Mlcp.UnitTests/       No I/O. Runs everywhere.
  Mlcp.IntegrationTests/  Testcontainers SQL Server + WireMock. TenantIsolation category.
docs/                   Authoritative specs. If code and docs disagree, raise it.
infra/                  Bicep, Entra runbook, local emulator config.
```

`Application` must never reference `Persistence` or `Integration.*`; the wiring happens in `Web` and `Sync`.

## Deviations from CLAUDE.md

Two, both recorded as ADRs rather than left as surprises:

- **[ADR-011](docs/06-decisions/ADR-011-net8-target-framework.md)** — targets `net8.0`, not the `net9.0` that `CLAUDE.md` fixes. The .NET 9 SDK is not available on the build machine. Reversing it is one edit in `Directory.Build.props` plus package bumps.
- **[ADR-012](docs/06-decisions/ADR-012-fluentassertions-version-pin.md)** — FluentAssertions pinned to 7.x. Version 8 requires a paid commercial licence.

## Build policy

`TreatWarningsAsErrors` and `EnforceCodeStyleInBuild` are on solution-wide, and NuGet audit failures are errors — that is how the known advisory in `Microsoft.Identity.Web` 3.5.0 was caught during setup. Package versions are centralised in `Directory.Packages.props`; do not add a version to a `.csproj`.
