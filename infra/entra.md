# Entra app registration runbook (Phase 0, P0-2)

1. Register app in the MLCP home tenant: name "MLCP", supported account types = "Accounts in any organizational directory (Any Microsoft Entra ID tenant - Multitenant)". No personal accounts.
2. Authentication: Web platform, redirect URIs per environment (`/signin-oidc`), front-channel logout. ID tokens on. Implicit off.
3. Branding: publisher domain = verified custom domain (not *.onmicrosoft.com); privacy statement URL; terms of service URL; logo.
4. Certificates: upload certificate from Key Vault for client credentials. No client secrets in production.
5. API permissions (application): Organization.Read.All, User.Read.All. Add Reports.Read.All later as tier 2 (separate consent). Delegated: User.Read, openid, profile, offline_access.
6. Expose an API: not required for single deployable.
7. App roles: Owner, Analyst, Viewer, SubscriptionManager (for in-app RBAC mapping if using Entra app roles; otherwise store roles in AppUser).
8. Publisher verification: Partner Center (Microsoft Cloud Partner Program) account; verifier needs Application Administrator or Cloud Application Administrator in Entra and CPP Partner Admin/Account Admin in Partner Center; MFA; link Partner ID under Branding → Publisher verification.
9. Admin consent URL for onboarding: https://login.microsoftonline.com/organizations/v2.0/adminconsent?client_id={clientId}&scope=https://graph.microsoft.com/.default&redirect_uri={redirect}&state={state}
10. Record client ID, tenant ID, certificate thumbprint in Key Vault; never in appsettings.
