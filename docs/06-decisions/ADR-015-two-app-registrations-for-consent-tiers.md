# ADR-015 — Tier 2 (usage reports) is a second app registration
Status: Accepted · Date: 2026-09-17 · Amends: ADR-008, docs/03 §4.3

## Context

ADR-008 and docs/03 §4.3 treat tier 1 (directory and licences) and tier 2 (`Reports.Read.All`) as separately grantable, revocable consents. For application permissions that is impossible with one registration. The [admin consent endpoint](https://learn.microsoft.com/en-us/entra/identity-platform/v2-admin-consent) requires `scope=https://graph.microsoft.com/.default`, and `/.default` grants every application permission statically configured on the registration. With one registration, `Reports.Read.All` is either:

- requested from every tenant at connection, which breaks least privilege and the ADR-008 "tier 1 only" promise; or
- never requested at all.

Adding it later to the one registration also changes what every existing tenant is asked for on its next consent.

Usage reports are the most privacy-sensitive thing MLCP reads, and the permission admins are most likely to refuse. Refusal must not block the licence floor.

## Decision

Two multi-tenant app registrations under the same verified publisher:

| Registration | Application permissions | Used for |
|---|---|---|
| **MLCP** (core) | `Organization.Read.All`, `User.Read.All`; delegated `openid profile User.Read offline_access` | Sign-in, universal floor, and the service principal that customers grant Azure RBAC and billing roles to (tiers 3–4) |
| **MLCP Usage Insights** | `Reports.Read.All` | Usage reports (tier 2) only |

- **Separate consent.** Each registration has its own admin-consent URL, built from its own client ID. The onboarding checklist offers tier 2 as a separate step (Guide D).
- **Credentials and tokens.** Both registrations use the same Key Vault certificate. `ITenantTokenProvider` acquires tokens per (tenant, app, audience), and `TokenAudience.GraphReports` resolves to the Usage Insights client ID.
- **Capability outcomes.** The usage probe runs with the Usage Insights token:
  - A token-endpoint `AADSTS700016`/`AADSTS7000229` for that app (no service principal in the tenant) means tier 2 is not granted, recorded as `GraphUsage = Unavailable(Tier2NotGranted)`.
  - A 403 means `Reports.Read.All` was removed.

  Neither outcome ever touches `Tenant.Status`.
- **Revocation.** Revoking Usage Insights leaves the core connection intact, as docs/03 §4.3 promises.
- **Permission set.** Tier 1 keeps `Organization.Read.All` and `User.Read.All`:
  - `Organization.Read.All` is required for `/directory/subscriptions` and `/organization`, and it also covers `/subscribedSkus`.
  - Adding the narrower `LicenseAssignment.Read.All` would add a permission without removing one. The per-call least-privilege permission is still documented in docs/02.
- **Admin-role claim.** The core registration sets `groupMembershipClaims: "DirectoryRole"`, so ID tokens carry `wids` (ADR-018).

## Consequences

+ True progressive, independently revocable consent.
+ Admins see a short, explainable permission list at connection.
− Two registrations to publisher-verify, brand and keep in step (runbook: `infra/entra.md`).
− Customers see two enterprise applications in their tenant. The Usage Insights one is named and described so its purpose is clear.
