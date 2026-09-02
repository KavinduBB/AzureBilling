# ADR-005 — Microsoft.Billing is the M365 commercial source for MCA tenants
Status: Accepted · Date: 2026-09-01

## Context
Graph `companySubscription` has no price, auto-renew, or term. `Microsoft.Billing` (2024-04-01) returns seat-based billing subscriptions with `autoRenew`, term dates, `cancellationAllowedEndDate`, and Transactions with `effectivePrice`/`marketPrice` per line item for MCA billing accounts.

## Decision
For MCA tenants, subscriptions, auto-renew, term, invoices, and seat prices come from `Microsoft.Billing`; Graph provides directory-side assignments. Cost Management provides Azure consumption. The two cost sources are stored separately (`TransactionFact`, `ConsumptionFact`) and never summed.
