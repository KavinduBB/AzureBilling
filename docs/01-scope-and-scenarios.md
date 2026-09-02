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
Business Standard/Premium bought online (MOSA → MCA at renewal), a few Copilot seats, pay-as-you-go Azure. Founder is usually Global Admin — connection is one click. Wants one page: people, licences, Azure spend, "is Copilot worth it," forecast, invoices.

### 3.2 Enterprise executive (5,000–100,000+ users)
EA or MCA-E, several tenants, large Azure, Copilot rollout. Not an admin — consent request flow brokers connection. Wants: utilisation per product and per business unit, £ waste, Copilot adoption, Azure by BU/service/environment, AI/API consumption, renewal risk with £ at stake, cross-tenant roll-up.

### 3.3 IT/finance analyst (any size)
The person who finds the product. Signs in with basic `User.Read`, sees a demo dashboard, sends the consent request to their admin.

## 4. Commercial scenarios and what each unlocks

| # | Scenario | Detection signal | Floor | Unlockable | Not available | UI message |
|---|---|---|---|---|---|---|
| A | Web-direct M365, **MOSA** | `subscribedSkus` present; no MCA billing account; no `ownerTenantId` | ✅ | Azure cost (PAYG) | M365 prices/invoices/auto-renew/lifecycle via API | "Microsoft moves accounts like yours to the Customer Agreement at renewal; pricing unlocks then. Enter seat prices meanwhile." |
| B | **MCA** direct | `billingAccounts.agreementType = MicrosoftCustomerAgreement` | ✅ | Prices, invoices, auto-renew, term, lifecycle ops, Azure cost | — | "Connect billing to see what you're paying." |
| C | **EA** | `agreementType = EnterpriseAgreement` | ✅ | Azure cost, Azure invoices, price sheet | M365 seat prices via API (enrolment-level) | "Upload your EA price sheet for licence cost analysis." |
| D | **CSP customer** | `companySubscription.ownerTenantId` populated | ✅ | Azure cost only if Azure Plan + partner enabled policy | Prices, invoices, auto-renew, lifecycle | "Your licences are managed by a partner. Enter what they charge you, or invite your partner." |
| E | CSP direct-bill partner | MPA billing account | ✅ | Everything via Partner Center + Graph partner billing | — | Phase 6 |
| F | CSP indirect reseller | Partner self-declares | ✅ (as a customer) | — | Any Partner Center API | "Partner features aren't available to indirect resellers (Microsoft restriction)." |

Mixed tenants (e.g. MCA for M365, CSP for Azure) are common; capability is tracked per data source, not per tenant.

## 5. What cannot be done (plain language)

1. **Prices/invoices for MOSA customers** — no API exists. Migrating to MCA fixes it at renewal.
2. **Costs for CSP customers** — the partner holds the billing relationship. Manual entry or partner bridge.
3. **Real-time cost** — Azure refreshes ~4 hours; CSP usage ~24 hours. Always show "as of".
4. **Lifecycle operations for non-MCA customers** — portal link only.
5. **Buying licences** — only MCA, and only as a gated financial transaction.
6. **Indirect resellers via Partner Center API** — Microsoft restriction.
7. **Named per-user usage while anonymisation is on** — aggregate works; per-person needs the admin's privacy opt-in.
8. **Azure cost for classic CSP Azure offer** — Azure Plan required.
9. **EA M365 seat prices automatically** — price sheet upload.

## 6. Resource-group and resource-level Azure cost

Supported for every Azure-connected tenant: cost per subscription → resource group → resource → meter; actual and amortised; by tag; trends and forecast at each level. Source: Cost Management Query (grouped by `ResourceGroupName`, `ResourceId`, `MeterCategory`) and Cost Details CSV. Caveats: reservation/savings-plan purchases, Marketplace SaaS, support plans, and some tenant-level charges have no resource group → "unallocated" bucket; some services don't emit tags; deleted resources persist in history by `ResourceId`; query at subscription scope grouped by RG, never per RG.

## 7. Out of scope (for now)

- Personal Microsoft accounts
- GitHub, LinkedIn, Xbox, or consumer subscriptions
- Third-party SaaS spend outside Azure Marketplace
- Multi-currency FX normalisation (currency is a dimension; sums are per currency)
- Power BI embedding
- Custom chargeback/showback allocation rules
