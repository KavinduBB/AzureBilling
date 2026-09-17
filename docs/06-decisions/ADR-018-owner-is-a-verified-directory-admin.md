# ADR-018 — Owner means "verified directory admin"; the consent callback is verified, not trusted
Status: Accepted · Date: 2026-09-17 · Amends: docs/03 §3 and §4.4, docs/05 P0-6

## Context

The review found two flaws in how Owners are created:

- **The consent callback trusts its query string.** Any signed-in user could:
  1. POST `/onboarding/connect` to obtain a valid `state`;
  2. call `/onboarding/consent-callback?admin_consent=True&state=…` directly, without visiting Entra;
  3. become Owner, cancel a pending deletion, and trigger Microsoft calls.

  The `state` was bound only to the tenant, reusable for 30 minutes.
- **The first person to sign in became Owner permanently**, admin or not. Owners can disconnect, which deletes the organisation's data.

## Decision

**Who is Owner**

- Owner is **derived from Entra, not stored as a grant**. A user is Owner when their ID token's `wids` claim contains a role that can grant tenant-wide consent to Microsoft Graph application permissions:
  - Global Administrator `62e90394-69f5-4237-9190-012177145e10`
  - Privileged Role Administrator `e8611ab8-c189-46e8-94e1-60213ab1f814`

  ([admin consent roles](https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/grant-admin-consent)).
- `wids` is emitted because the core registration sets `groupMembershipClaims: "DirectoryRole"` ([group claims and app roles](https://learn.microsoft.com/en-us/security/zero-trust/develop/configure-tokens-group-claims-app-roles)). Microsoft Learn documents `wids` explicitly for access tokens; for ID tokens the evidence is the group-claims page ("ID or access tokens") and the Blazor Entra roles guide. **P0-2 acceptance includes confirming `wids` in a real ID token from a second test tenant.** If it is absent, the fallback is a delegated `GET /me/memberOf/microsoft.graph.directoryRole` at sign-in, which needs only `User.Read`.
- The role is re-evaluated at **every sign-in**. `AppUser.Role` is recorded for display and audit, and is upgraded to or downgraded from Owner to match. A demoted admin loses Owner at their next sign-in, and destructive actions re-check the live claim (below).
- Everyone else from the tenant is **Viewer** until an Owner changes their role (Analyst in Phase 1, P1-10). Pre-consent sign-ins are Viewer.

**Consent callback hardening**

1. **Bound, single-use `state`.** `state` = Data-Protection-protected `{tid, oid, nonce, expiry (10 min)}`. The nonce is stored in `IDistributedCache` (Redis) under `consent-nonce:{nonce}` and deleted on first use. A replay, or a callback from a different user or tenant, is refused.
2. **Only admins can start admin consent.** `POST /onboarding/connect` is refused for non-admins, who get the "Ask my admin" path.
3. **Microsoft's `tenant` parameter must match.** It must equal the caller's `tid` (defence in depth; the tenant is still taken from the token).
4. **Consent is verified, not assumed.** The callback does not mark the tenant consented. Instead, the app acquires an **app-only** token for the tenant and calls `GET /organization`:
   - Success → `Tenant.ConfirmConsent`, and discovery is enqueued.
   - `AADSTS700016` / `AADSTS7000229` (propagation) → the tenant enters `ConsentPendingVerification`, and a verification job is scheduled at +2 min, +5 min and +15 min.
   - Anything else → "Consent could not be confirmed".
5. **The callback never cancels a scheduled deletion.** Reconnecting during the grace period is the explicit, Owner-only `POST /onboarding/cancel-disconnect`.
6. **Discovery is queued, not run in the request** (CLAUDE.md rule 5), except for the single verification call above.

**Destructive actions**

- `Disconnect` and `CancelDisconnect` require a **live** admin `wids` claim in the current session, and a re-authentication within the last 15 minutes (`max_age`, checked via `auth_time`).
- Every Owner action writes an audit attempt row first (ADR-019).

**Consent requests by email**

The "Ask my admin" email is rate-limited:

- 3 sends per requesting user per 24 h.
- 10 per tenant per 24 h.
- 15-minute resend cooldown.
- Recipients must be in one of the tenant's verified domains (from the `/organization` probe).
- No user-controlled text goes in the subject.
- A resend to a different address creates a new request rather than silently reusing the old one.

## Consequences

+ Closes the Owner escalation and the deletion-cancel bypass.
+ The tenant's own directory remains the source of truth for who administers it.
− Organisations whose MLCP owners are not Global or Privileged Role Admins must wait for Phase 1 role management (an Owner assigning Analyst). Only admins can disconnect, which is intended.
− Requires `groupMembershipClaims` on the registration (runbook step).
