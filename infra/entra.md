# Entra app registration runbook (Phase 0, P0-2)

MLCP uses **two** multi-tenant app registrations under the same verified publisher (ADR-015).
Tier 2 (usage reports) needs its own registration because admin consent with `/.default` grants
every application permission configured on a registration
([admin consent endpoint](https://learn.microsoft.com/en-us/entra/identity-platform/v2-admin-consent)).

| Registration | Application permissions | Delegated | Used for |
|---|---|---|---|
| **MLCP** (core) | `Organization.Read.All`, `User.Read.All` | `openid`, `profile`, `offline_access`, `User.Read` | Sign-in; the universal floor; the service principal customers grant Azure RBAC and billing roles to (tiers 3–4) |
| **MLCP Usage Insights** | `Reports.Read.All` | none | Usage reports (tier 2) only |

Client ids are **configuration, not secrets**. They are set as `AzureAd:ClientId` and
`AzureAd:UsageInsightsClientId` (deployment variables `ENTRA_CLIENT_ID` and
`USAGE_INSIGHTS_CLIENT_ID`; locally, user-secrets). Neither registration has a client secret in
any deployed environment.

## 1. Core registration: "MLCP"

1. **Register.** In the MLCP home tenant, go to *Entra admin center → App registrations → New
   registration*:
   - Name: `MLCP`.
   - Supported account types: *Accounts in any organizational directory (Any Microsoft Entra ID
     tenant – Multitenant)*, so `signInAudience` is `AzureADMultipleOrgs`. No personal accounts.
2. **Authentication.** Add the *Web* platform.
   - **Redirect URIs** (one set per public host):

     | Host | URIs |
     |---|---|
     | Global Front Door (`https://app.<domain>`) | `/signin-oidc`, `/onboarding/consent-callback` |
     | EU stack (`https://eu.app.<domain>`) | `/signin-oidc`, `/onboarding/consent-callback` |
     | US stack (`https://us.app.<domain>`) | `/signin-oidc`, `/onboarding/consent-callback` |
     | Local (`https://localhost:7003`) | `/signin-oidc`, `/onboarding/consent-callback` (dev registration only; see below) |

     Each regional deployment prints its own URIs as the `entraRedirectUris` output.
   - **Front-channel logout URL:** `https://app.<domain>/signout-callback-oidc`.
   - **ID tokens:** on. **Implicit access tokens:** off.
3. **Token configuration.**
   - Set `groupMembershipClaims` to `DirectoryRole`, either in the manifest or under *Token
     configuration → Add groups claim → Directory roles*. This makes tokens carry the `wids`
     claim, which is how MLCP decides who is an Owner (ADR-018).
   - Add the optional **ID token** claim `auth_time`. Disconnect and cancel-disconnect require a
     sign-in within the last 15 minutes, and the app checks that against `auth_time` (ADR-018).
   - About `wids`:
     - The [access token claims reference](https://learn.microsoft.com/en-us/entra/identity-platform/access-token-claims-reference)
       documents `wids` with `groupMembershipClaims` = `All` or `DirectoryRole`.
     - For **ID tokens** this is shown by the Zero Trust
       [group claims guide](https://learn.microsoft.com/en-us/security/zero-trust/develop/configure-tokens-group-claims-app-roles),
       not stated in the ID-token reference. **Verify it** on the first sign-in from a test tenant.
4. **App roles.** Define exactly one app role (ADR-023):

   | Display name | Value | Allowed member types | Description |
   |---|---|---|---|
   | Subscription Manager | `SubscriptionManager` | Users/Groups | Can change quantities, auto-renew and cancellations from MLCP (Phase 5). |

   `Owner`, `Analyst` and `Viewer` are **in-app** roles and are **not** Entra app roles:
   - Owner is derived from `wids` (ADR-018).
   - Analyst and Viewer are stored in `AppUser.Role`.

   Customers assign `SubscriptionManager` in *their* tenant under *Enterprise applications →
   MLCP → Users and groups* (Guide F).
5. **API permissions.**
   - Microsoft Graph application permissions: `Organization.Read.All`, `User.Read.All`.
   - Microsoft Graph delegated permissions: `openid`, `profile`, `offline_access`, `User.Read`.
   - Do **not** add `Reports.Read.All` here; it belongs to the Usage Insights registration.
   - Do not add `LicenseAssignment.ReadWrite.All` until Phase 5 (P5-5). At that point it is
     requested incrementally as a delegated permission.
6. **Expose an API.** Not required: MLCP is a single deployable (ADR-001).
7. **Certificate credential.**
   - Upload the public key of the Key Vault certificate `mlcp-client` from each region's vault
     (`infra/deploy.md` §4).
   - Both regions may use one shared certificate or one each; register every public key that is
     in use.
   - **No client secrets in any deployed environment.**
8. **Branding and properties.**
   - Publisher domain: the verified custom domain, not `*.onmicrosoft.com`.
   - Also set the logo, home page, privacy statement URL (`https://app.<domain>/privacy`) and
     terms of service URL.

## 2. Tier-2 registration: "MLCP Usage Insights"

1. **Register.** Name: `MLCP Usage Insights`. Same account type (multitenant).
   - The description and logo should make its purpose obvious. Customers see it as a second
     enterprise application.
2. **Authentication.**
   - Web platform with the redirect URI `/onboarding/usage-insights-callback` on each host listed
     above (global, EU, US, and local for the development registration).
   - No sign-in happens with this registration, and it needs no ID tokens.
3. **API permissions.**
   - Application: Microsoft Graph `Reports.Read.All` only.
   - No delegated permissions.
4. **Certificate credential.** The same Key Vault certificate(s) as the core registration (ADR-015).
5. **Branding.** Same publisher domain, privacy and terms URLs.
6. **No app roles** and no `groupMembershipClaims`.

## 3. Publisher verification (both registrations)

Required before launch (ADR-008). Requirements are from
[publisher verification](https://learn.microsoft.com/en-us/entra/identity-platform/publisher-verification-overview)
and [mark an app as publisher verified](https://learn.microsoft.com/en-us/entra/identity-platform/mark-app-as-publisher-verified):

- A Microsoft AI Cloud Partner Program (CPP) account with a verified Partner One ID. This must be
  the partner global account (PGA), not a location id.
- The person verifying:
  - holds **Application Administrator** or **Cloud Application Administrator** in Entra;
  - holds **CPP Partner Admin** or **Account Admin** in Partner Center;
  - signs in with **MFA**.
- The app is registered in a tenant associated with the PGA.
- The publisher domain is set and is not `*.onmicrosoft.com`. It matches the domain of the CPP
  verification email, or a DNS-verified custom domain.
- On each registration, go to *Branding & properties → Publisher verification*, enter the Partner
  One ID, and save.

## 4. Admin consent URLs

Built by the app from each registration's client id
([admin consent endpoint](https://learn.microsoft.com/en-us/entra/identity-platform/v2-admin-consent)):

```
https://login.microsoftonline.com/organizations/v2.0/adminconsent
  ?client_id={clientId}
  &scope=https://graph.microsoft.com/.default
  &redirect_uri={https://<host>/onboarding/consent-callback}          (core)
                {https://<host>/onboarding/usage-insights-callback}   (Usage Insights)
  &state={protected state}
```

- `/.default` requests every application permission configured on that registration, which is
  why the two tiers are two registrations.
- The callback returns `admin_consent`, `tenant`, `scope` and `state`. It is **not** proof of
  consent: the app confirms consent with an app-only `GET /organization` (ADR-018).
- Tenant-wide consent to Microsoft Graph **application** permissions requires a **Global
  Administrator** or a **Privileged Role Administrator**. Cloud Application Administrator and
  Application Administrator cannot grant Graph app roles
  ([grant admin consent](https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/grant-admin-consent)).

## 5. Local development registration

Use a **separate** registration for local development, so its redirect URIs and its client secret
never touch the production registrations:

- Redirect URIs: `https://localhost:7003/signin-oidc` and
  `https://localhost:7003/onboarding/consent-callback`. Use the `https` launch profile of
  `Mlcp.Web`.
- A second development registration for Usage Insights with
  `https://localhost:7003/onboarding/usage-insights-callback` (only needed for tier-2 work).
- A short-lived client secret is acceptable **for this registration only**. Store it with
  `dotnet user-secrets` (README).
- Apply the same `groupMembershipClaims` and app role settings as production.

## 6. What never goes into configuration

- Client secrets for the production registrations (there are none).
- Certificate private keys. They stay in Key Vault, and the apps load them with their managed
  identities.
- Certificate thumbprints. The apps load by certificate **name** (`Mlcp:ClientCertificateName`,
  `AzureAd:ClientCredentials:0:KeyVaultCertificateName`).
