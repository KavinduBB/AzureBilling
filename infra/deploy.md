# Deployment runbook (Azure)

One stack per region. A tenant's region is chosen at connection and never migrated silently
(`docs/03-architecture.md` §5.3), so each region is independent: its own database, its own keys,
its own data.

## What gets created

| Resource | Why | Public access |
|---|---|---|
| Container Apps environment + 2 apps | Web (ingress) and sync worker (no ingress) | Web only |
| Azure SQL, serverless GP | Tenant data | Disabled — private endpoint |
| Azure Cache for Redis | MSAL token cache, read cache | Disabled — private endpoint |
| Service Bus Premium + queue | Sync job fan-out, sessions per tenant | Disabled — private endpoint |
| Key Vault | Client certificate, Data Protection key | Disabled — private endpoint |
| Storage account | Data Protection key ring | Disabled — private endpoint |
| Log Analytics + App Insights | Traces and logs | n/a |
| User-assigned managed identity | The identity customers grant roles to | n/a |

SQL uses **Entra-only authentication**. There is no SQL login, so there is no database password
anywhere in configuration, in Key Vault, or in this repository.

## Order of operations

Some steps cannot be expressed in Bicep because they depend on resources the template creates.
They are listed here rather than hidden in a script so it is clear what is manual and why.

### 1. Prerequisites

- An Entra app registration (`infra/entra.md`). You need its **client id**.
- An Entra **group** that will administer SQL. Bicep sets it as the server's Entra admin; using
  a group rather than a person means the server does not lose its administrator when someone
  leaves.
- A container registry, and the workload identity granted `AcrPull` on it.

### 2. Deploy the stack

```bash
RG=mlcp-prod-weu
az group create -n $RG -l westeurope

az deployment group create \
  -g $RG \
  --template-file infra/main.bicep \
  --parameters \
      environmentName=prod \
      entraClientId=<client-id> \
      sqlAdminGroupObjectId=<group-object-id> \
      sqlAdminGroupName='MLCP SQL Admins' \
      containerRegistryServer=<registry>.azurecr.io \
      webImage=<registry>.azurecr.io/mlcp-web:<tag> \
      syncImage=<registry>.azurecr.io/mlcp-sync:<tag>
```

The deployment outputs `entraRedirectUri`, `workloadPrincipalId` and `keyVaultName`.

### 3. Complete the Entra registration

Add the deployment's `entraRedirectUri` to the app registration's web redirect URIs. Until this
is done, sign-in fails with `AADSTS50011`.

### 4. Upload the client certificate

The certificate is the application's credential for calling Microsoft on behalf of every
customer tenant. It is never a client secret (CLAUDE.md rule 4).

```bash
az keyvault certificate create \
  --vault-name <keyVaultName> \
  --name mlcp-client \
  --policy "$(az keyvault certificate get-default-policy)"

# Upload the public key to the app registration.
az keyvault certificate download --vault-name <keyVaultName> --name mlcp-client --file mlcp-client.cer
az ad app credential reset --id <client-id> --cert "@mlcp-client.cer" --append
```

Note the expiry. An expired certificate stops **all** synchronisation for **all** tenants at
once, and the symptom — every tenant flagged `NeedsReconsent` — looks like a customer problem
rather than ours. Set a calendar reminder for 30 days before.

### 5. Create the database user for the workload identity

Bicep can create the server and the database but not a contained user inside it. Connect as a
member of the SQL admin group, from inside the VNet or via a jump host, and run:

```sql
CREATE USER [mlcp-prod-identity] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [mlcp-prod-identity];
ALTER ROLE db_datawriter ADD MEMBER [mlcp-prod-identity];
ALTER ROLE db_ddladmin ADD MEMBER [mlcp-prod-identity];
```

`db_ddladmin` is required only if migrations run as the workload identity. If migrations run as
a separate deployment identity, grant it there instead and leave the workload with reader and
writer alone — the smaller grant is preferable.

> The audit table is append-only by design (`docs/03-architecture.md` §8). Once the schema is
> stable, replace `db_datawriter` with explicit grants and give `AuditLog` INSERT and SELECT
> only, so a compromised application cannot rewrite its own audit trail.

### 6. Apply migrations

```bash
export MLCP_MIGRATIONS_CONNECTION="Server=tcp:<sqlServerFqdn>,1433;Initial Catalog=mlcp;Authentication=Active Directory Default;Encrypt=True;"
dotnet dotnet-ef database update --project src/Mlcp.Persistence --startup-project src/Mlcp.Web
```

This creates the schema **and** the row-level security policy. Verify the policy exists before
serving traffic:

```sql
SELECT name, is_enabled FROM sys.security_policies WHERE name = 'TenantSecurityPolicy';
```

If that returns no rows, stop. Isolation layer 3 is absent and the deployment must not take
customer traffic.

### 7. Email (optional but needed before inviting admins)

Create an Azure Communication Services resource with an email domain, verify the domain, then
redeploy with `emailEndpoint` and `emailSenderAddress`. Until configured, consent request links
are written to the log instead of sent, and the application logs a warning at startup saying so.

## Customer-side onboarding

Customers grant roles to `workloadPrincipalId` in **their own** tenants:

- **Azure costs**: `infra/customer-rbac.bicep` assigns Cost Management Reader at their tenant
  root management group.
- **Prices and invoices**: a billing role on their billing account, granted by hand
  (`docs/07-onboarding-guides.md` Guide C).

Neither is granted by admin consent. That is the single most common misunderstanding during
onboarding, which is why the checklist shows all three authorisation systems separately.

## Verifying a deployment

```bash
curl -sf https://<webFqdn>/health && echo OK
az containerapp logs show -g $RG -n mlcp-prod-web --tail 50
az containerapp logs show -g $RG -n mlcp-prod-sync --tail 50
```

The sync worker logs a capability-discovery sweep within 15 minutes of starting. Seeing that is
the signal that the identity, the database and Service Bus are all reachable.
