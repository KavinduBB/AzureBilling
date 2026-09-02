# MLCP Documentation — Index

**Prepared:** 1 September 2026
**Status:** Final pre-implementation documentation. Authoritative for development.

These documents consolidate the research, feasibility assessment, and architecture work into a single set for implementation. Earlier research drafts (v1, v2, public-SaaS addendum) are superseded by this set; where they conflict, this set wins.

| File | Purpose | Read when |
|---|---|---|
| `../CLAUDE.md` | Rules and conventions Claude Code follows in every session | Always |
| `01-scope-and-scenarios.md` | What the product is, who it serves, what it can and cannot do | Before any feature work |
| `02-api-reference.md` | Every Microsoft API the platform uses: endpoint, permission, agreement type, limits | Before any integration work |
| `03-architecture.md` | Auth, multi-tenancy, sync, resilience, security | Before Phase 0 |
| `04-data-model.md` | Schema, keys, indexes, isolation, history | Before Persistence work |
| `05-implementation-plan.md` | Phases → epics → tasks with acceptance criteria | Start of every task |
| `06-decisions/` | Architecture Decision Records | When questioning a design choice |
| `07-onboarding-guides.md` | Customer-facing remediation guides (Graph consent, Azure RBAC, billing role) | Phase 0/1 UI work |

## Working with Claude Code

Suggested opening prompt for a session:

```
Read CLAUDE.md and docs/05-implementation-plan.md. We are in Phase 0.
Pick up task P0-3 (tenant isolation layers). Show me the plan before writing code.
```

For integration work:

```
Read docs/02-api-reference.md §3.2 and docs/03-architecture.md §4.
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
| MOSA | Microsoft Online Subscription Agreement — legacy web-direct billing account. Being migrated to MCA at renewal. |
| MCA | Microsoft Customer Agreement — modern direct agreement. Billing account → billing profile → invoice section. Richest API surface. |
| MCA-E / MCA-online | Field-led vs self-service flavours of MCA. API parity assumed; validate (ADR-007). |
| EA | Enterprise Agreement — enrolment → department → account. Legacy Enterprise Reporting APIs retired. |
| MPA | Microsoft Partner Agreement — CSP partner's billing account. |
| CSP | Cloud Solution Provider — partner channel. Customers of a CSP have no billing relationship with Microsoft. |
| NCE | New Commerce Experience — current CSP licensing model with 7-day cancellation windows. |
| GDAP | Granular Delegated Admin Privileges — how partners get scoped, time-bound access to customer tenants. |
| Capability profile | Per-tenant record of which data sources are reachable and why. Drives every UI state. |
| Universal floor | The feature set available to every tenant via Graph alone. |
| Unlock tier | A consent/role grant that enables additional data (see 03-architecture §4.3). |
