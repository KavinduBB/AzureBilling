# ADR-002 — Progressive enhancement over a uniform data path
Status: Accepted · Date: 2026-09-01

## Context
Microsoft exposes licence data uniformly via Graph, but cost, price, invoice, and lifecycle data only for certain agreement types (MCA/EA/MPA) and only after RBAC/billing-role grants. Target audience is any licence holder.

## Decision
Every tenant receives the universal floor (Graph). Each additional data source is a separate, revocable unlock resolved into a `TenantCapabilityProfile`. Providers return typed `CapabilityUnavailable(Reason)` instead of nulls; UI renders explained states.

## Consequences
+ Product works for 100% of tenants on day one; honest UX.
− Every feature must define its unavailable state; enforced by PR rule in CLAUDE.md.
