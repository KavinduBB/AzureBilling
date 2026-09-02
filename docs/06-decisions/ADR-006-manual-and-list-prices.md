# ADR-006 — Manual and list-price entry is a core feature, not a fallback
Status: Accepted · Date: 2026-09-01

## Context
MOSA, EA, and CSP-customer tenants have no API-sourced seat prices. They are the majority at launch.

## Decision
Ship `ListPriceReference` (ops-maintained) and per-SKU manual override with provenance badges in Phase 1. `DerivedUnitPrice` priority: BillingTransaction > PartnerShared > Manual > ListPriceReference.
