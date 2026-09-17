# ADR-020 — "Undetermined" is an agreement type; MOSA requires positive evidence
Status: Accepted · Date: 2026-09-17 · Amends: docs/01 §4 (detection signals), docs/04 §1

## Context

docs/01 §4 detects MOSA by "no MCA billing account visible". A billing account is only visible to a principal that holds a billing role, and immediately after connection nobody has granted one. The resolver therefore classified almost every new direct customer as MOSA. The checklist then hid Guide C, the one guide that would reveal the truth.

## Decision

`AgreementType` gains **`Undetermined`**: "the tenant's agreement cannot be seen with the grants we hold". The resolver classifies only on positive evidence:

| Result | Evidence |
|---|---|
| `Mca` | A visible billing account with `agreementType = MicrosoftCustomerAgreement`, **or** any subscription whose `billingProperty.billingAccountAgreementType` is MCA |
| `Ea` | Same signals with `EnterpriseAgreement` |
| `Mpa` | A visible MPA billing account (partner, Phase 6) |
| `CspManaged` | `companySubscription.ownerTenantId` set to another tenant, **or** a subscription with `MicrosoftPartnerAgreement` billing property |
| `Mosa` | A subscription whose `billingProperty.billingAccountAgreementType` is `MicrosoftOnlineServicesProgram`, **or** a billing role probe that succeeded (so billing is visible) and found no MCA/EA account |
| `Undetermined` | None of the above: Graph is readable, but there is no Azure visibility and no billing visibility |

**UI for `Undetermined`**

- The checklist shows the **Guide C** row as actionable, with the copy: "We can't see how your organisation buys Microsoft licences yet. If you have a Microsoft Customer Agreement, grant billing read access to unlock prices and invoices. If you bought online before 2023, pricing may not be available yet — you can enter seat prices meanwhile."
- Guide B stays actionable.
- The manual-price path is offered to every agreement type other than MCA with a billing role granted.
- Mixed tenants keep a per-source classification, as today. `Tenant.AgreementTypePrimary` is the most specific non-`Undetermined` value found.

## Consequences

+ No customer is told pricing is impossible while the unlock that would provide it is hidden.
− One more state for the dashboard's explanatory panels (rule 9), which is copy only.
