# ADR-023 — SubscriptionManager is an Entra app role, independent of Owner
Status: Accepted · Date: 2026-09-17 · Amends: infra/entra.md step 7, docs/04 §1, ADR-010

## Context

`infra/entra.md` left open whether roles are Entra app roles or database values. The code stores `AppUser.IsSubscriptionManager` and requires the holder to also be Owner.

## Decision

- **Where the role lives.** `SubscriptionManager` is an **app role** defined on the core app registration (`allowedMemberTypes: User`, value `SubscriptionManager`). The customer's admin assigns it in *Enterprise applications → MLCP → Users and groups*. It arrives in the ID token's `roles` claim.
- **Nothing in the database grants it.** `AppUser.IsSubscriptionManager` is removed. Assignment and removal stay entirely under the customer's control and appear in their Entra audit log.
- **It is independent of Owner** (separation of duties). A finance user can hold it without being a directory admin, and an Owner does not have it implicitly.
- **Write-tier gates (ADR-010), all required:**
  - the `roles` claim contains `SubscriptionManager`;
  - the tenant feature flag `LifecycleOps` is on (only an Owner can turn it on);
  - the user's own delegated billing role permits the operation (Microsoft enforces this);
  - pre-flight window checks and a confirmation with the financial impact;
  - an audit Attempt row is written before the call.
- **In-app roles.** Owner (derived, ADR-018), Analyst and Viewer (stored in `AppUser.Role`, managed by Owners in P1-10) remain application roles.

## Consequences

+ No MLCP-side path can grant write capability.
− The customer admin performs one extra step in Entra, documented in Guide F (Phase 5).
