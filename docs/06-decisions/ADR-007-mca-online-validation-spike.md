# ADR-007 — Validate MCA-online parity with Microsoft.Billing before Phase 3
Status: Proposed · Date: 2026-09-01

## Context
Documentation does not explicitly distinguish MCA billing accounts created via the Microsoft 365 admin center from field-led MCA-E for `Microsoft.Billing` API purposes.

## Decision
Before starting Phase 3, run a two-day spike against a real M365-admin-center-created MCA tenant: (1) `GET billingAccounts` returns it with seat-based `billingSubscriptions`; (2) Transactions return `effectivePrice` for an M365-only account; (3) `billingSubscriptionAliases` PUT succeeds for a seat-based SKU. Record results here and update `02-api-reference.md`.

## If it fails
Phase 3 applies to MCA-E only; MCA-online tenants use manual/list prices; lifecycle ops for those tenants are portal links.
