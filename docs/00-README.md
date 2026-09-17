# MLCP Documentation — Index

**Prepared:** 1 September 2026 · **Revised:** 17 September 2026 (hardening pass, ADR-014 to ADR-026)
**Status:** Authoritative for development. If code and docs disagree, raise it; do not silently pick one.

This set consolidates the research, the feasibility assessment and the architecture work. It
supersedes the earlier drafts (v1, v2, and the public-SaaS addendum); where they conflict, this set
wins. Where a document and an ADR disagree, the newer ADR wins until the document is fixed.

| File | Purpose | Read when |
|---|---|---|
| `../CLAUDE.md` | Rules and conventions for every Claude Code session | Always |
| `01-scope-and-scenarios.md` | What the product is, who it serves, and what it can and cannot do | Before any feature work |
| `02-api-reference.md` | Every Microsoft API the platform uses: endpoint, permission, agreement type, limits, source link | Before any integration work |
| `03-architecture.md` | Auth, multi-tenancy, regions, sync, resilience, security | Before Phase 0 |
| `04-data-model.md` | Schema, keys, indexes, isolation, database roles | Before Persistence work |
| `05-implementation-plan.md` | Phases → epics → tasks, with acceptance criteria | At the start of every task |
| `06-decisions/` | Architecture Decision Records | When questioning a design choice |
| `07-onboarding-guides.md` | Customer-facing remediation guides (A–F) | Phase 0/1 UI work |
| `../infra/deploy.md` | Azure deployment runbook and GitHub configuration | Before deploying |
| `../infra/entra.md` | App registration runbook (two registrations) | Before P0-2 / deploying |

## Decisions (ADRs)

| ADR | Decision | Status |
|---|---|---|
| [001](06-decisions/ADR-001-single-deployable.md) | Single ASP.NET Core deployable (MVC + API) | Accepted |
| [002](06-decisions/ADR-002-progressive-enhancement.md) | Progressive enhancement over a uniform data path | Accepted |
| [003](06-decisions/ADR-003-tenant-isolation-four-layers.md) | Shared schema with four-layer tenant isolation | Accepted, hardened by 026 |
| [004](06-decisions/ADR-004-sync-not-live.md) | Dashboards read SQL; Microsoft is called only by sync jobs and explicit actions | Accepted |
| [005](06-decisions/ADR-005-microsoft-billing-as-m365-commerce-source.md) | Microsoft.Billing is the M365 commercial source for MCA | Accepted |
| [006](06-decisions/ADR-006-manual-and-list-prices.md) | Manual and list-price entry is a core feature | Accepted |
| [007](06-decisions/ADR-007-mca-online-validation-spike.md) | Validate MCA-online parity before Phase 3 | Proposed |
| [008](06-decisions/ADR-008-publisher-verification-prerequisite.md) | Publisher verification and non-admin sign-up are launch prerequisites | Accepted, amended by 015, 025 |
| [009](06-decisions/ADR-009-monetisation-channel.md) | Monetisation channel | Proposed |
| [010](06-decisions/ADR-010-write-tier-controls.md) | Write operations are delegated-only and gated | Accepted, detailed by 023 |
| [011](06-decisions/ADR-011-net8-target-framework.md) | Target net8.0 | **Superseded by 014** |
| [012](06-decisions/ADR-012-fluentassertions-version-pin.md) | Pin FluentAssertions to 7.x | Accepted |
| [013](06-decisions/ADR-013-sync-load-modes.md) | Validation gate volume checks apply to full loads only | Accepted, amended by 024 |
| [014](06-decisions/ADR-014-net10-target-framework.md) | Target net10.0 (LTS) | Accepted |
| [015](06-decisions/ADR-015-two-app-registrations-for-consent-tiers.md) | Tier 2 (usage reports) is a second app registration | Accepted |
| [016](06-decisions/ADR-016-failure-classification-and-reconsent-recovery.md) | Failure classification and re-consent recovery | Accepted |
| [017](06-decisions/ADR-017-cost-management-scope-ladder.md) | Cost Management scope ladder | Accepted |
| [018](06-decisions/ADR-018-owner-is-a-verified-directory-admin.md) | Owner is a verified directory admin; the consent callback is verified | Accepted |
| [019](06-decisions/ADR-019-audit-log-append-only-and-deletion.md) | Audit log: two rows per action, append-only, purged with the tenant | Accepted |
| [020](06-decisions/ADR-020-undetermined-agreement-type.md) | `Undetermined` agreement type; MOSA needs positive evidence | Accepted |
| [021](06-decisions/ADR-021-regional-routing.md) | Regional stacks behind one Front Door, with a global tenant→region directory | Accepted |
| [022](06-decisions/ADR-022-organisation-rollup-under-rls.md) | Organisation roll-up through bilateral grants enforced by RLS | Accepted (design; P3-8) |
| [023](06-decisions/ADR-023-subscription-manager-is-an-entra-app-role.md) | SubscriptionManager is an Entra app role | Accepted |
| [024](06-decisions/ADR-024-validation-gate-per-run-and-period.md) | Validation gate: per-run mode, period baselines, override | Accepted |
| [025](06-decisions/ADR-025-demo-dashboard-in-phase-1.md) | The demo dashboard ships in Phase 1 | Accepted |
| [026](06-decisions/ADR-026-deployment-identities-and-migrations.md) | Separate identities, in-VNet migrations, Azure Managed Redis | Accepted |

## Working with Claude Code

Suggested opening prompt for a session:

```
Read CLAUDE.md and docs/05-implementation-plan.md. We are in Phase 0.
Pick up task P0-3 (tenant isolation layers). Show me the plan before writing code.
```

For integration work:

```
Read docs/02-api-reference.md §2.2 and docs/03-architecture.md §4.
Implement IAzureBillingProvider.ListBillingSubscriptionsAsync with WireMock fixtures
from tests/Fixtures/MicrosoftApi/billing-subscriptions-mca.json.
```

For anything touching tenant data:

```
Run the TenantIsolation test category before and after. If any test fails, stop.
```

## Glossary

| Term | Meaning |
|---|---|
| MOSA / MOSP | Microsoft Online Subscription Agreement / Program. The legacy web-direct billing account. Microsoft is moving these accounts to MCA; Learn documents no fixed timing |
| MCA | Microsoft Customer Agreement. The modern direct agreement: billing account → billing profile → invoice section. The richest API surface |
| MCA-E / MCA-online | Field-led and self-service flavours of MCA. API parity is assumed but must be validated (ADR-007) |
| EA | Enterprise Agreement: enrolment → department → account. The legacy Enterprise Reporting APIs are retired |
| MPA | Microsoft Partner Agreement: a CSP partner's billing account |
| CSP | Cloud Solution Provider, the partner channel. CSP customers have no billing relationship with Microsoft |
| NCE | New Commerce Experience: the current CSP licensing model, with 7-day cancellation windows |
| GDAP | Granular Delegated Admin Privileges: how partners get scoped, time-bound access to customer tenants |
| Capability profile | The per-tenant record of which data sources are reachable, and why. It drives every UI state |
| Universal floor | The feature set every tenant gets from Graph alone (core registration) |
| Unlock tier | A consent or role grant that enables additional data (docs/03 §4.3) |
| Core / Usage Insights | The two Entra app registrations (ADR-015) |
| Enterprise application | The service principal an app registration creates in a customer's tenant. Customers grant Azure and billing roles to MLCP's |
| Regional stack | A complete MLCP deployment in one region (EU, US), behind Front Door (ADR-021) |
| Scope ladder | The order in which cost queries pick a scope: billing profile/account → root management group → per subscription (ADR-017) |
