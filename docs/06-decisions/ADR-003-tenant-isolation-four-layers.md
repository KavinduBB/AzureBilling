# ADR-003 — Shared schema with four-layer tenant isolation
Status: Accepted, hardened by ADR-026 (system bypass requires the worker database role) · Date: 2026-09-01

## Decision
Shared database/schema with: (1) TenantId on every business table, (2) EF global query filter from validated `tid`, (3) SQL Row-Level Security on SESSION_CONTEXT, (4) API-boundary check returning 404. A CI test category attempts cross-tenant reads through every repository/endpoint.

## Alternatives rejected
Database-per-tenant: operationally heavy at thousands of free-tier tenants. Schema-per-tenant: EF migration complexity. Either can be revisited for regulated enterprise tiers.
