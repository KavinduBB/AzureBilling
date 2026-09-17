# ADR-022 — Organisation roll-up: explicit, bilateral, read-only view grants enforced by RLS
Status: Accepted (design; implementation in Phase 3, P3-8) · Date: 2026-09-17 · Amends: docs/03 §5.2, docs/04 §1

## Context

docs/03 §5.2 lets an Owner in an organisation's home tenant view all linked tenants. That appears to conflict with rule 2 (`TenantId` only from `tid`) and with an RLS predicate that admits exactly one tenant. It is also unclear which tenant owns billing-account rows that serve several associated tenants.

## Decision

- **Rule 2 is unchanged.** The *acting* tenant is always the `tid`. Cross-tenant visibility comes only from data the linked tenant's own admin approved.
- **`OrganizationViewGrant`** (a global table, not tenant-scoped) holds `ViewerTenantId`, `TargetTenantId`, `GrantedByObjectId` (an admin of the **target** tenant), `GrantedUtc` and `RevokedUtc`. A grant is created only when the **target** tenant's verified admin (ADR-018) approves a link request from the viewer tenant. The approval is bilateral: request in one tenant, approval in the other. Either side can revoke.
- **RLS**
  - The **FILTER** predicate becomes:
    ```sql
    @TenantId = SESSION_CONTEXT('TenantId')
    OR (SESSION_CONTEXT('OrgView') = 1
        AND EXISTS (SELECT 1 FROM dbo.OrganizationViewGrant g
                    WHERE g.ViewerTenantId = SESSION_CONTEXT('TenantId')
                      AND g.TargetTenantId = @TenantId AND g.RevokedUtc IS NULL))
    OR <system clause per ADR-026>
    ```
  - The **BLOCK** predicate stays strictly single-tenant, so roll-up views are read-only by construction.
  - `OrgView` is stamped read-only on the connection only for requests to endpoints marked `[OrganizationRollup]`, which are GET only.
- **EF.** The global filter mirrors the predicate using a request-scoped set of visible tenant IDs, loaded once per request from `OrganizationViewGrant`. Layer 4 (`[TenantScoped]`) accepts a target tenant only if it is in that set, and otherwise returns 404.
- **Shared billing data.** Billing-account-level rows (`BillingAccount`, `BillingProfile`, `InvoiceSection`, `Invoice`, `TransactionFact`) are owned by the **billing tenant**: the tenant whose service principal holds the billing role and whose credentials synced them. Associated tenants see them only through a roll-up grant from the billing tenant. `AssociatedBillingTenant` lists the associations, so that tenant can be prompted for consent (P3-8).
- **Tests.** The isolation suite gains roll-up cases:
  - no grant → nothing visible;
  - revoked grant → nothing visible;
  - granted → read-only access, and a write is blocked;
  - `OrgView` not stamped → single tenant only.

## Consequences

+ Enterprise roll-up without weakening rule 2 or the write path.
− The predicate gains an indexed EXISTS: an index on `OrganizationViewGrant (ViewerTenantId, TargetTenantId) WHERE RevokedUtc IS NULL`, measured before P3-8 ships.
