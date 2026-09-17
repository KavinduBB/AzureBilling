# ADR-008 — Publisher verification and non-admin sign-up are launch prerequisites
Status: Accepted, amended by ADR-015 (tier 2 is a second registration) and ADR-025 (demo dashboard is Phase 1) · Date: 2026-09-01

## Context
Multi-tenant apps from unverified publishers show a risk warning; tenants with risk-based step-up consent block user consent; admins may refuse. The first user is usually not an admin.

## Decision
Complete CPP enrolment and publisher verification in Phase 0. Implement basic sign-in for non-admins with an admin consent request flow and a demo dashboard. Tiered consent: request only tier 1 at connection.
