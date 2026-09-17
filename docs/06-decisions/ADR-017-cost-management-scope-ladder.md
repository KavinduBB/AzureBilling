# ADR-017 — Cost Management queries use a per-tenant scope ladder
Status: Accepted · Date: 2026-09-17 · Amends: CLAUDE.md rule 8, docs/01 §6, docs/05 P4-1/P4-3, Guide B

## Context

`CLAUDE.md` rule 8 says to query at billing-account or management-group scope and never loop per subscription. Guide B says one Cost Management Reader assignment at the Tenant Root Group "covers everything". Microsoft Learn ([Understand and work with scopes](https://learn.microsoft.com/en-us/azure/cost-management-billing/costs/understand-work-scopes), updated 2026-03-25) says:

- "Management groups aren't currently supported in Cost Management features for Microsoft Customer Agreement subscriptions."
- "Cost Management doesn't support Management groups in CSP scopes."
- Management groups "only include usage-based charges"; reservations and Marketplace purchases need the billing scope.
- The Cost Details API doesn't support management groups for EA or MCA.
- Billing-scope access comes from **billing roles**, not Azure RBAC. For MCA, the billing account and billing profile are the scopes that include purchases.

So for MCA and CSP-managed tenants, which are the priced scenarios, the literal rule 8 leaves no permitted scope.

## Decision

A `CostScopeResolver` in `Mlcp.Application/Costs` picks, per tenant and per sync, the **widest supported scope the app can read**, in this order:

| Rung | Scope | When | Access needed |
|---|---|---|---|
| 1 | MCA **billing profile** (one query per profile), or EA **billing account** | Agreement is MCA/EA and a billing role is granted (tier 4) | MCA: Billing profile reader / Billing account reader. EA: Enterprise read-only |
| 2 | **Root management group** | Agreement is EA or MOSP (pay-as-you-go), and Cost Management Reader is at root MG | RBAC (tier 3) |
| 3 | **Per subscription** | MCA or CSP (Azure plan) subscriptions with RBAC but no billing role; or no MG assignment | RBAC on each subscription |

**Rules**

- **Grouping.** Every query is grouped server-side by `SubscriptionId` and `ResourceGroupName` (plus `ResourceId`/`MeterCategory` for detail). Never loop per resource group.
- **Rung 3 budget.** Allowed only when rungs 1–2 are unavailable. Queries run sequentially per tenant under a QPU budget: 12 per 10 s, 60 per minute, 600 per hour ([Cost Management automation](https://learn.microsoft.com/en-us/azure/cost-management-billing/costs/manage-automation)). Each sync logs the rung chosen. If a tenant exceeds 300 subscriptions on rung 3, the capability detail recommends a billing role or Exports.
- **Unallocated purchases.** When rung 2 or 3 is used, purchases (reservations, Marketplace) are not visible. The dashboard shows an explanatory "Purchases need billing access" panel (rule 9), not a silent gap.
- **Cost Details.** Cost Details reports (P4-3) run per billing profile (rung 1) or per subscription. Never per management group.
- **Onboarding copy.** Guide B keeps the root-MG assignment as the fastest path. It notes that MCA and CSP customers also need Guide C (billing role) to see purchases, and that MLCP will fall back to per-subscription queries meanwhile.
- **Agreement lookup.** The rung is recomputed on each capability discovery, and stored as `TenantCapabilityProfile` detail. For each subscription, `billingProperty.billingAccountAgreementType` (already probed) tells the resolver which subscriptions are MCA/MPA.

`CLAUDE.md` rule 8 becomes: "Query at the widest supported scope chosen by `CostScopeResolver` (ADR-017); always group server-side; never loop per resource group; per-subscription queries only on rung 3 and under the QPU budget; always send `ClientType`."

## Consequences

+ Works for every agreement type Microsoft supports, with the fewest queries possible.
+ The rule remains strict where a wider scope exists.
− Rung 3 is O(subscriptions) and slower, so large MCA/CSP tenants are nudged to grant a billing role.
