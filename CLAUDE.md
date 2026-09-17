# CLAUDE.md — Microsoft License & Cost Management Platform (MLCP)

This file is read by Claude Code at the start of every session. Keep it current.

## What this project is

A public, multi-tenant SaaS where any organization that holds Microsoft licences can sign in with Entra ID and track licences, usage, subscriptions, Azure costs, and (where their agreement allows) prices, invoices, and renewals. Progressive enhancement: every tenant gets a universal floor from Microsoft Graph; richer data unlocks by agreement type and granted permissions.

Read `docs/00-README.md` first, then `docs/01-scope-and-scenarios.md`. Do not start implementation work without reading `docs/05-implementation-plan.md` for the current phase.

## Stack (fixed — do not substitute)

- .NET 10 (LTS), ASP.NET Core — single deployable (ADR-014; supersedes ADR-011): MVC + Razor views for UI, API controllers under `/api/v1`
- Entity Framework Core 10 → Azure SQL Database (SQL Server locally via Docker)
- Microsoft.Identity.Web (MSAL) for Entra ID; multi-tenant app, `AzureADMultipleOrgs`
- Microsoft.Graph SDK (v5+), Azure.ResourceManager.* SDKs (Billing, CostManagement, Consumption); raw `HttpClient` where an SDK lags the REST surface
- Azure Service Bus for job fan-out; long-running Container App worker with sessions, Container Apps Jobs for migrations/overflow (ADR-026; locally: `dotnet run` worker project)
- Azure Managed Redis (StackExchange.Redis, Entra auth) for the encrypted MSAL distributed token cache, consent nonces and read cache (ADR-026)
- Azure Key Vault + Managed Identity for secrets; locally: user-secrets
- Polly for resilience; Serilog for structured logging
- Frontend: server-rendered Razor + htmx + Chart.js. **No SPA framework.**
- Tests: xUnit, FluentAssertions, Testcontainers (SQL Server), WireMock.Net for Microsoft API fakes

## Solution layout

```
src/
  Mlcp.Web/                 MVC + API host. Thin controllers. No business logic.
  Mlcp.Application/         Use cases, DTOs, capability gates, orchestration. No EF, no HTTP.
  Mlcp.Domain/              Entities, value objects, enums, domain rules. No dependencies.
  Mlcp.Persistence/         EF Core DbContext, configurations, migrations, RLS setup, repositories.
  Mlcp.Integration.Graph/   IGraphLicensingProvider, IGraphUsageProvider (Microsoft Graph).
  Mlcp.Integration.Azure/   IAzureBillingProvider, IAzureCostProvider (Microsoft.Billing, CostManagement, Consumption).
  Mlcp.Integration.Partner/ IPartnerCenterProvider (deferred — Phase 6).
  Mlcp.Sync/                Worker host: job scheduler, Service Bus consumers, SyncRun pipeline.
  Mlcp.Shared/              Cross-cutting: tenant context, correlation, redaction, Polly policies.
tests/
  Mlcp.UnitTests/
  Mlcp.IntegrationTests/    Testcontainers SQL + WireMock Microsoft APIs. Includes tenant-isolation suite.
docs/                       Authoritative specs. If code and docs disagree, raise it; do not silently pick one.
infra/                      Bicep for Azure resources. One-click RBAC template for customer onboarding.
```

Dependency direction: `Web → Application → Domain`; `Persistence`, `Integration.*`, `Sync` depend on `Application`/`Domain` and are wired via DI in `Web` and `Sync`. Never reference `Persistence` or `Integration.*` from `Application`.

## Non-negotiable rules

1. **Tenant isolation is four layers and all four ship together:** `TenantId` column on every business table; EF global query filter from `ITenantContext`; SQL Row-Level Security on `SESSION_CONTEXT('TenantId')`; API-boundary check returning **404** (not 403) on tenant mismatch. `tests/Mlcp.IntegrationTests/TenantIsolation/` must pass on every PR.
2. **`ITenantContext.TenantId` comes only from the validated `tid` claim.** Never from route, header, query, or body.
3. **No user access/refresh tokens in application tables.** MSAL distributed cache only, encrypted with Data Protection keys from Key Vault.
4. **No secrets in config or code.** Key Vault via Managed Identity; `dotnet user-secrets` locally.
5. **Never call Microsoft APIs from a request handler to render a dashboard.** Dashboards read SQL. Sync jobs call Microsoft. The only interactive Microsoft calls are onboarding probes and explicit user-triggered actions (e.g. invoice PDF download, lifecycle ops).
6. **All sync writes go through the SyncRun pipeline:** staging table → validation gate → single transaction (inside the EF execution strategy) containing the MERGE on natural key **and** the run's `Succeeded` status → staging cleanup. Live tables are never partially written. The gate blocks empty responses and >50% drops vs the last successful run's **staged** count, for full loads, per period, with an audited operator override (ADR-013, ADR-024). One run per (tenant, job) at a time.
7. **Every Microsoft call goes through the Polly policy set in `Mlcp.Shared.Resilience`:** honour `Retry-After`, `x-ms-ratelimit-microsoft.consumption-retry-after` and `x-ms-ratelimit-microsoft.costmanagement-qpu-retry-after`; exponential backoff with jitter; per-(tenant, provider) circuit breaker; never retry 401/403. Failures are classified per ADR-016: only a lost core grant or a 401/403 on a Graph floor call marks the tenant `NeedsReconsent`; a 401/403 elsewhere marks only that capability unavailable; transient errors keep the previous verdict; MLCP credential failures alert ops and never flag tenants. `NeedsReconsent` tenants are re-probed automatically.
8. **Cost Management Query:** always send `ClientType: Mlcp` header; query at the widest supported scope chosen by `CostScopeResolver` (billing profile/account → root management group → per subscription; ADR-017); always group server-side by `SubscriptionId`/`ResourceGroupName`; never loop per resource group; per-subscription queries only when no wider scope is supported, sequential and under the QPU budget.
9. **Unavailable data is a typed reason, not null.** `Unavailable*Provider` classes return `CapabilityUnavailable(Reason)`. UI renders the reason. Empty charts are a bug.
10. **Money is `decimal`. Every money column has an adjacent currency column. Never sum across currencies.**
11. **Nullable means unknowable.** `AutoRenewEnabled = null` means "no API exposes this for this tenant." Never default to false.
12. **Write operations (purchase, cancel, quantity, auto-renew toggle) are delegated-identity only,** behind the Entra app role `SubscriptionManager` (ADR-023), per-tenant feature flag, pre-flight window check, confirmation with financial impact, and an audit Attempt row written before the outbound call and an Outcome row after it. `AuditLog` is append-only, enforced by database permissions (ADR-019).
13. **Do not persist invoice download URLs.** They are short-lived SAS tokens. Fetch on demand.
14. **Redact before logging.** `Authorization` headers, tokens, secrets, SAS URLs. Log correlation IDs.
15. **Do not invent Microsoft API endpoints, permissions, or fields.** If it isn't in `docs/02-api-reference.md` or verifiable on Microsoft Learn, stop and ask.

## Conventions

- C# 13, nullable enabled, `TreatWarningsAsErrors` on.
- Async all the way; `CancellationToken` on every I/O method.
- Domain entities are private-set; mutation through methods.
- One EF migration per PR; name `YYYYMMDDHHMM_Description`.
- Feature folders in `Mlcp.Application` (`Licensing/`, `Costs/`, `Billing/`, `Onboarding/`, `Sync/`).
- API DTOs are records suffixed `Dto`; never expose entities.
- Integration tests use WireMock recordings in `tests/Fixtures/MicrosoftApi/`; never hit live Microsoft in CI.
- Commit messages: `type(scope): summary` — `feat`, `fix`, `refactor`, `test`, `docs`, `infra`.

## How to work in this repo

- Before starting a task, read the matching epic in `docs/05-implementation-plan.md` and check acceptance criteria.
- If a task requires a Microsoft API behaviour not documented in `docs/02-api-reference.md`, add it there with a source link first.
- When you add a capability, add the corresponding `Unavailable*` reason and the UI explanatory state in the same PR.
- Run `dotnet test --filter Category=TenantIsolation` before any PR touching Persistence, Application, or Web.
- Update `docs/06-decisions/` with an ADR when you make a choice that future you would question.
- Owner is a verified directory admin (`wids` claim), never the first user to sign in; the consent callback is verified with an app-only call, never trusted (ADR-018).
- Build with the .NET 10 SDK (`global.json`). Integration tests need Docker, or set `MLCP_TEST_SQL` to a disposable SQL Server connection string.

## Current phase

**Phase 0 — Foundation + trust.** See `docs/05-implementation-plan.md` §Phase 0. Nothing user-facing ships until Phase 0 acceptance criteria are green.
