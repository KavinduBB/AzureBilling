# 01 — Scope, Personas & Scenarios

## 1. Product definition

MLCP is a public, self-service, multi-tenant SaaS. Any organization holding Microsoft commercial licences can sign in with Microsoft Entra ID and, after an admin connects the tenant, track:

- Licences: purchased, assigned, available, per SKU and per user
- Usage: which licensed services people actually use (Teams, Exchange, SharePoint, OneDrive, M365 Apps, Copilot)
- Subscriptions: status, trial flag, renewal date, term, auto-renew (where exposed)
- Azure costs: by subscription, resource group, resource, service, tag; trends; forecast; reservations
- Microsoft 365 costs: seat prices, invoices, blended spend (where the agreement exposes them; manual/list price otherwise)
- Optimisation: unused seats, seats on disabled accounts, inactive assignees, overlapping SKUs, Azure savings opportunities, cost anomalies
- Lifecycle operations: auto-renew toggle, quantity changes, cancellation (MCA only, gated)

Target customers are **licence holders**, not Microsoft partners. A partner module is a later optional phase.

## 2. Design principle: progressive enhancement

```
UNIVERSAL FLOOR (Graph; every tenant)
  licences · assignments · renewal dates · usage · waste detection
        │
        ├── + MCA billing role  → seat prices · invoices · auto-renew · lifecycle ops
        ├── + Azure RBAC        → Azure cost · forecast · reservations · anomalies
        └── + manual/list price → £ value of waste for everyone else
```

Every tenant gets the floor on day one. Everything else is an explicit, revocable unlock. Every unavailable dataset renders as an explained state with the reason.

## 3. Personas

### 3.1 Startup founder (15–80 users)
Business Standard/Premium bought online (MOSA, or MCA if bought or renewed recently), a few Copilot seats, pay-as-you-go Azure. Founder is usually Global Admin — connection is one click. Wants one page: people, licences, Azure spend, "is Copilot worth it," forecast, invoices.

### 3.2 Enterprise executive (5,000–100,000+ users)
EA or MCA-E, several tenants, large Azure, Copilot rollout. Not an admin — consent request flow brokers connection. Wants: utilisation per product and per business unit, £ waste, Copilot adoption, Azure by BU/service/environment, AI/API consumption, renewal risk with £ at stake, cross-tenant roll-up.

### 3.3 IT/finance analyst (any size)
The person who finds the product. Signs in with basic `User.Read`, sees a labelled preview (screenshots in Phase 0; the interactive demo dashboard is P1-11, ADR-025), and sends the consent request to their admin.

## 4. Commercial scenarios and what each unlocks

Classification uses **positive evidence only** (ADR-020). A billing account is visible only to a principal holding a billing role, and just after connection nobody has granted one. So "no MCA billing account visible" is **not** evidence of MOSA.

| # | Scenario | Detection signal (positive evidence) | Floor | Unlockable | Not available | UI message |
|---|---|---|---|---|---|---|
| A | Web-direct M365, **MOSA** | A subscription whose `billingProperty.billingAccountAgreementType = MicrosoftOnlineServicesProgram`, **or** a billing probe that succeeded (billing is visible) and found no MCA/EA account | ✅ | Azure cost (pay-as-you-go) | M365 prices/invoices/auto-renew/lifecycle via API | "Pricing isn't available through Microsoft's APIs for your agreement type. Enter seat prices meanwhile." |
| B | **MCA** direct | A visible billing account with `agreementType = MicrosoftCustomerAgreement`, **or** any subscription whose `billingAccountAgreementType` is MCA | ✅ | Prices, invoices, auto-renew, term, lifecycle ops, Azure cost | — | "Connect billing to see what you're paying." |
| C | **EA** | Same signals with `EnterpriseAgreement` | ✅ | Azure cost, Azure invoices, price sheet | M365 seat prices via API (enrolment-level) | "Upload your EA price sheet for licence cost analysis." |
| D | **CSP customer** | `companySubscription.ownerTenantId` set to another tenant, **or** a subscription with a `MicrosoftPartnerAgreement` billing property | ✅ | Azure cost only for an Azure plan (Cost Management supports CSP customers only when they have an MCA), per subscription | Prices, invoices, auto-renew, lifecycle | "Your licences are managed by a partner. Enter what they charge you, or invite your partner." |
| E | CSP direct-bill partner | A visible MPA billing account | ✅ | Everything via Partner Center + Graph partner billing | — | Phase 6 |
| F | CSP indirect reseller | Partner self-declares | ✅ (as a customer) | — | Any Partner Center API | "Partner features aren't available to indirect resellers (Microsoft restriction)." |
| U | **Undetermined** | None of the above: Graph is readable, but there is no Azure visibility and no billing visibility | ✅ | Guide C (billing role) and Guide B (Azure RBAC) are both shown as actionable; manual prices offered | — | "We can't see how your organisation buys Microsoft licences yet. If you have a Microsoft Customer Agreement, grant billing read access to unlock prices and invoices. If you bought online before 2023, pricing may not be available yet — you can enter seat prices meanwhile." |

- **Mixed tenants** are common (e.g. MCA for M365, CSP for Azure). Capability is tracked per data source, not per tenant.
- `Tenant.AgreementTypePrimary` is the most specific non-`Undetermined` value found.
- **Manual prices** are offered to every tenant except an MCA tenant with a billing role granted.

Signal sources:
- [Billing Accounts - List](https://learn.microsoft.com/en-us/rest/api/billing/billing-accounts/list?view=rest-billing-2024-04-01)
- [Billing Property - Get](https://learn.microsoft.com/en-us/rest/api/billing/billing-property/get?view=rest-billing-2024-04-01)
- [companySubscription](https://learn.microsoft.com/en-us/graph/api/resources/companysubscription?view=graph-rest-1.0)
- [Cost Management scopes](https://learn.microsoft.com/en-us/azure/cost-management-billing/costs/understand-work-scopes)

## 5. What cannot be done (plain language)

1. **Prices/invoices for MOSA customers** — no API exists. Microsoft is moving direct customers to the Customer Agreement, and pricing unlocks once the account is on MCA. This is not guaranteed at any particular renewal: Microsoft Learn documents no fixed timing. `billingProperty.isTransitionedBillingAccount` shows when it has happened.
2. **Prices for CSP customers** — the partner holds the billing relationship. Manual entry or partner bridge.
3. **Real-time cost** — Azure refreshes ~4 hours; CSP usage ~24 hours. Always show "as of".
4. **Lifecycle operations for non-MCA customers** — portal link only.
5. **Buying licences** — only MCA, and only as a gated financial transaction.
6. **Indirect resellers via Partner Center API** — Microsoft restriction.
7. **Named per-user usage while anonymisation is on** — aggregate works; per-person needs the admin's privacy opt-in.
8. **Azure cost for classic CSP Azure offer** — Azure Plan required.
9. **EA M365 seat prices automatically** — price sheet upload.

## 6. Resource-group and resource-level Azure cost

Supported for every Azure-connected tenant: cost per subscription → resource group → resource → meter; actual and amortised; by tag; trends and forecast at each level.

**Sources.** Cost Management Query (grouped server-side by `SubscriptionId` / `ResourceGroupName`, and `ResourceId` / `MeterCategory` for detail) and Cost Details reports.

**Scope.** The widest supported scope the app can read, chosen per tenant by `CostScopeResolver` (ADR-017):

| Rung | Scope | When |
|---|---|---|
| 1 | MCA billing profile / EA billing account | A billing role is granted |
| 2 | Root management group | EA or pay-as-you-go subscriptions, with Cost Management Reader at the root |
| 3 | Each subscription, sequentially, under the QPU budget | MCA and CSP subscriptions without a billing role, because management groups are not supported for them |

- Never loop per resource group.
- Cost Details reports run per billing profile or per subscription, never per management group.

**Caveats.**
- Reservation and savings-plan purchases, Marketplace SaaS, support plans and some tenant-level charges have no resource group, so they land in an "unallocated" bucket.
- Management-group and subscription scopes include only usage-based charges. Purchases appear only at the billing scope (rung 1). Without it, the dashboard shows a "Purchases need billing access" panel.
- Some services don't emit tags.
- Deleted resources persist in history by `ResourceId`.

Source: [Understand and work with scopes](https://learn.microsoft.com/en-us/azure/cost-management-billing/costs/understand-work-scopes).

## 7. Out of scope (for now)

- Personal Microsoft accounts
- GitHub, LinkedIn, Xbox, or consumer subscriptions
- Third-party SaaS spend outside Azure Marketplace
- Multi-currency FX normalisation (currency is a dimension; sums are per currency)
- Power BI embedding
- Custom chargeback/showback allocation rules
