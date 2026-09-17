# Deployment runbook (Azure)

MLCP runs as **one global layer and one stack per region** (ADR-021, ADR-026).

- **Global** (`infra/global.bicep`, deployed once):
  - Azure Front Door (`app.<domain>`, plus `eu.app.<domain>` and `us.app.<domain>`);
  - a geo-redundant storage table, `TenantRegions`, that maps `tid → region`.
- **Regional** (`infra/main.bicep` with `main.eu.bicepparam` / `main.us.bicepparam`): a complete,
  independent stack. A tenant's region is chosen at connection and never migrated silently
  (`docs/03-architecture.md` §5.3).

No step needs workstation access to a private endpoint. Everything that touches SQL runs as a
Container Apps job inside the VNet. The only exception is creating the Key Vault certificate, which
uses a temporary, IP-restricted allow rule (§4).

## What gets created

### Per region

| Resource | Purpose | Public access |
|---|---|---|
| Container Apps environment (VNet-integrated) | Hosts everything below | n/a |
| `…-web` container app | MVC + API. `activeRevisionsMode: Multiple`; new revisions start at 0% traffic | Ingress (HTTPS) |
| `…-sync` container app | Scheduler and Service Bus worker (min = max = 1) | None (no ingress) |
| `…-migrate` job (manual) | EF Core migration bundle, as the migrator identity | None |
| `…-create-users` job (manual) | `infra/sql/create-users.sql` via go-sqlcmd, as the migrator identity | None |
| Azure SQL, serverless GP, **Entra-only** | Tenant data | Disabled (private endpoint) |
| Azure Managed Redis, **access keys disabled** | MSAL token cache, consent nonces, read cache | Disabled (private endpoint) |
| Service Bus Premium + `mlcp-sync-jobs` (sessions, duplicate detection) | Sync job fan-out | Disabled (private endpoint) |
| Key Vault (RBAC, purge protection) | Client certificate, Data Protection key | Disabled (private endpoint) |
| Storage account (shared key disabled) | Data Protection key ring | Disabled (private endpoint) |
| Log Analytics + Application Insights | Logs and traces | n/a |
| Identities `…-web-identity`, `…-worker-identity`, `…-migrator-identity` | Workload identities (below) | n/a |

### Global

| Resource | Purpose |
|---|---|
| Storage account, `Standard_RAGZRS`, shared key disabled, table `TenantRegions` | `tid → region` directory. It holds only the tenant GUID, the region code and a timestamp. Access is Entra-only, with RBAC scoped to the table |
| Front Door profile | Global endpoint (latency-routed, session affinity for the OIDC round trip) and one endpoint per region; health probe `/health/ready` |

### Identities and grants (ADR-026)

| Identity | Grants | Database |
|---|---|---|
| web | Service Bus **Data Sender** (queue); Key Vault **Secrets User** + **Certificate User**; **Crypto User** on the Data Protection key; **Blob Data Contributor** on the Data Protection container; **Storage Table Data Contributor** on `TenantRegions`; **AcrPull**; Redis access policy `default` | `mlcp_web_user` → role `mlcp_web` |
| worker | Service Bus **Data Sender** + **Data Receiver** (queue); Key Vault **Secrets User** + **Certificate User**; **Storage Table Data Contributor** on `TenantRegions`; **AcrPull**; Redis access policy `default` | `mlcp_worker_user` → roles `mlcp_worker`, `mlcp_system` |
| migrator | **AcrPull**; member of the **SQL admin Entra group** (manual step, §3) | SQL admin (DDL) |

No identity has Service Bus Data Owner. The workload users have no DDL rights and no
`db_datareader`/`db_datawriter`: their permissions come only from the MLCP roles, which the
migrations create (ADR-019).

> **Customers never grant anything to these identities.** In their own tenant, customers grant
> Azure RBAC and billing roles to the **MLCP enterprise application** (the service principal
> created by admin consent). The apps act as that application using the Key Vault certificate.

## Prerequisites

1. **Two Entra app registrations** (`infra/entra.md`). Note both client ids.
2. **A SQL admin Entra group**, e.g. `MLCP SQL Admins (prod)`, security-enabled. Bicep sets it as
   the server's Entra admin. A group means the server keeps an administrator when people leave.
3. **A container registry** in its own resource group, shared by all regions. Bicep grants
   `AcrPull` on it; you do not.
4. **A public DNS zone** for `<domain>`, where you can create CNAME and TXT records.
5. **Resource groups:** `mlcp-<env>-global`, `mlcp-<env>-eu` (West Europe), `mlcp-<env>-us`
   (East US 2).
6. **GitHub** configuration (§9).

## First deployment

The order matters: each step needs outputs from the one before it.

```
1. global.bicep (directory only)
2. main.bicep per region
3. add each migrator identity to the SQL admin group
4. Key Vault certificate → both app registrations
5. migrate job → create-users job
6. global.bicep again, now with regional origins (Front Door)
7. custom domains, then Entra redirect URIs
8. from now on: the Deploy workflow
```

### 1. Global directory

```bash
export MLCP_ENVIRONMENT=prod
az group create -n mlcp-prod-global -l westeurope
az deployment group create -g mlcp-prod-global --parameters infra/global.bicepparam \
  --query properties.outputs
```

With `MLCP_ORIGIN_EU`/`MLCP_ORIGIN_US` unset, only the storage account and table are created.
Record `regionDirectoryTableUri` and `regionDirectoryStorageAccountId`. They become the GitHub
variables `REGION_DIRECTORY_TABLE_URI` and `REGION_DIRECTORY_STORAGE_ACCOUNT_ID`.

### 2. Regional stack

The first run is done by hand, because the workflow expects the migrate job to exist.

1. Build and push the three images, tagged `<tag>`:
   - `src/Mlcp.Web/Dockerfile`
   - `src/Mlcp.Sync/Dockerfile`
   - `src/Mlcp.Persistence/Dockerfile.migrate`
2. Export the variables and deploy:

```bash
export MLCP_ENVIRONMENT=prod
export MLCP_ENTRA_CLIENT_ID=<core client id>
export MLCP_USAGE_INSIGHTS_CLIENT_ID=<usage insights client id>
export MLCP_SQL_ADMIN_GROUP_OBJECT_ID=<group object id>
export MLCP_SQL_ADMIN_GROUP_NAME='MLCP SQL Admins (prod)'
export MLCP_ACR_NAME=<registry> MLCP_ACR_RESOURCE_GROUP=<registry rg>
export MLCP_WEB_IMAGE=<registry>.azurecr.io/mlcp-web:<tag>
export MLCP_SYNC_IMAGE=<registry>.azurecr.io/mlcp-sync:<tag>
export MLCP_MIGRATE_IMAGE=<registry>.azurecr.io/mlcp-migrate:<tag>
export MLCP_WEB_REVISION_SUFFIX=init-<tag7>          # lower case, unique
export MLCP_REGION_DIRECTORY_TABLE_URI=<from step 1>
export MLCP_REGION_DIRECTORY_STORAGE_ACCOUNT_ID=<from step 1>
export MLCP_PUBLIC_DOMAIN=<domain>
export MLCP_CUSTOMER_RBAC_TEMPLATE_URL=https://raw.githubusercontent.com/<owner>/<repo>/main/infra/customer-rbac.json

az group create -n mlcp-prod-eu -l westeurope
az deployment group create -g mlcp-prod-eu --parameters infra/main.eu.bicepparam \
  --query properties.outputs
```

Record these outputs: `webFqdn`, `migratorPrincipalId`, `keyVaultName`, `migrateJobName`,
`createUsersJobName`, `entraRedirectUris`, `usageInsightsRedirectUri`.

**Expected on the first run:**
- The web and worker revisions report unhealthy until steps 4–5 are done. There is no schema,
  no database user and no certificate yet, so `/health/ready` fails. This is expected.
- If the deployment fails pulling an image, the `AcrPull` assignment has not propagated yet
  (it can take a few minutes). Re-run the same command.

Repeat for `us` with `infra/main.us.bicepparam` and `mlcp-prod-us` (East US 2).

### 3. Migrator into the SQL admin group

This is not in Bicep, because it needs Microsoft Graph rights that the deployment principal should
not hold. A group owner runs:

```bash
az ad group member add --group <SQL admin group object id> --member-id <migratorPrincipalId>
```

Do this for each region's migrator.

### 4. Client certificate

The certificate is MLCP's credential for **every** customer tenant, for both registrations
(ADR-015). It is never a client secret (CLAUDE.md rule 4).

Creating and downloading a certificate are Key Vault **data-plane** operations, and the vault has
public access disabled. Open a temporary allow rule for your IP only, then close it:

```bash
KV=<keyVaultName>; MYIP=$(curl -s https://api.ipify.org)
# Needs "Key Vault Certificates Officer" on the vault (grant it to yourself temporarily, e.g. via PIM).
az keyvault update -n $KV --public-network-access Enabled --default-action Deny -o none
az keyvault network-rule add -n $KV --ip-address $MYIP -o none

az keyvault certificate create --vault-name $KV --name mlcp-client \
  --policy "$(az keyvault certificate get-default-policy)"
az keyvault certificate download --vault-name $KV --name mlcp-client --file mlcp-client.cer

# Close the vault again. The next deployment also resets both settings.
az keyvault network-rule remove -n $KV --ip-address $MYIP -o none
az keyvault update -n $KV --public-network-access Disabled -o none
```

Upload the public key to **both** registrations:

```bash
az ad app credential reset --id <core client id> --cert "@mlcp-client.cer" --append
az ad app credential reset --id <usage insights client id> --cert "@mlcp-client.cer" --append
```

**Expiry.** An expired certificate stops **all** sync for **all** tenants.
- ADR-016 classifies this as `PlatformCredential`: ops is alerted, and tenants are **not** flagged
  `NeedsReconsent`.
- Key Vault can renew the certificate automatically, but the new public key must still be uploaded
  to both registrations.
- Set a reminder 30 days before `expires`, and upload the renewed key before the old one expires.

**App configuration.** The certificate is exposed to Key Vault's secret API as a secret with the
same name, and that secret holds the private key.
- The web app loads the certificate through `AzureAd:ClientCredentials:0` (`SourceType=KeyVault`).
- The worker loads it through `Mlcp:ClientCertificateName`.
- If the app adds Key Vault as a configuration provider, it **must filter out** that
  certificate-backed secret. It must never be loaded into `IConfiguration`, where it would be
  readable through configuration dumps and logs. This is app code (`Mlcp.Web`/`Mlcp.Sync`), not
  infrastructure.

### 5. Schema and database users

The migrate job runs first. The roles `mlcp_web`, `mlcp_worker` and `mlcp_system` are created by
migrations, and the user script refuses to run without them.

```bash
.github/scripts/run-job.sh mlcp-prod-eu mlcp-prod-eu-migrate
.github/scripts/run-job.sh mlcp-prod-eu mlcp-prod-eu-create-users
```

Without the script, start the job directly and poll its execution:

```bash
az containerapp job start -g mlcp-prod-eu -n mlcp-prod-eu-migrate --query name -o tsv
az containerapp job execution show -g mlcp-prod-eu -n mlcp-prod-eu-migrate \
  --job-execution-name <name> --query properties.status
```

**How the create-users job works:**
- It creates `mlcp_web_user` and `mlcp_worker_user` with `WITH SID = <identity client id>, TYPE = E`.
- It adds them to their roles.
- It fails if the web user is ever in `mlcp_system`.
- It is idempotent: re-run it whenever an identity is recreated.
- It avoids `FROM EXTERNAL PROVIDER`. When the caller is a service principal (the migrator), that
  form needs the SQL server's own identity in the Entra *Directory Readers* role
  ([CREATE USER](https://learn.microsoft.com/en-us/sql/t-sql/statements/create-user-transact-sql)).

**Verify both** from the job logs in Log Analytics (`ContainerAppConsoleLogs_CL`, filtered by job
name):
- the migrate output ends with `Done.`;
- the create-users output lists the two users and their roles.

The `TenantSecurityPolicy` security policy is created by the migrations. CI checks for it on every
PR. If a regional database ever lacks it, isolation layer 3 is absent and the region must not take
customer traffic.

> **Upgrading from the single-identity deployment:** drop the old shared user (e.g.
> `[mlcp-prod-identity]`) once both new users exist. Do this with a one-off script run by the
> create-users job image.

### 6. Front Door

Set each region's `webFqdn`, then deploy global again:

```bash
export MLCP_ORIGIN_EU=<eu webFqdn> MLCP_ORIGIN_US=<us webFqdn>
az deployment group create -g mlcp-prod-global --parameters infra/global.bicepparam \
  --query properties.outputs
```

Outputs: `frontDoorHostName` and `regionalFrontDoorHostNames`.

### 7. Custom domains and Entra

1. In the Front Door profile, add the custom domains:
   - `app.<domain>` on the global endpoint;
   - `eu.app.<domain>` and `us.app.<domain>` on the regional endpoints.

   Use Front Door managed certificates. Create the TXT validation records and CNAMEs it asks for,
   then associate each domain with its endpoint's `default` route.
2. Register the redirect URIs listed in `infra/entra.md` §1 (`entraRedirectUris` per region)
   on the core registration, and `/onboarding/usage-insights-callback` on each host on the Usage
   Insights registration.
   Until this is done, sign-in fails with `AADSTS50011`.
3. Open `https://app.<domain>/health/ready` and each regional host. All should return 200.

### 8. Email (needed before inviting admins)

1. Create an Azure Communication Services resource with a verified email domain.
2. Give the web identity an Azure role on that resource that allows sending email with Entra
   authentication. Check the current role name in the Azure Communication Services docs; it is
   not verified here.
3. Set the GitHub variables `EMAIL_ENDPOINT` and `EMAIL_SENDER_ADDRESS`, then redeploy.

Until this is done, consent request links are written to the log instead of sent, and the app
logs a startup warning.

## Routine deployments (GitHub Actions)

`.github/workflows/deploy.yml`, started manually for one environment and region. It runs only from
`main`.

1. **Build and push** `mlcp-web`, `mlcp-sync` and `mlcp-migrate`, tagged with the first 12
   characters of the commit SHA.
2. **What-if** against the regional resource group, in environment `<env>-plan`.
3. **Approval.** The `release` job uses the protected environment `<env>`; a reviewer approves
   after reading the what-if.
4. **Deploy infrastructure.**
   - The new web revision `<app>--r<run>-<attempt>-<sha7>` is created at **0%** traffic, with the
     label `candidate`.
   - The worker keeps its current image.
5. **Migrate job.** The workflow starts it and polls until `Succeeded`; any other terminal state
   fails the run.
6. **Smoke test.** `GET https://<new revision FQDN>/health/ready` must return 200.
7. **Traffic.** 100% moves to the new revision.
8. **Worker.** Updated to the new image, now that the schema leads the code.
9. **Deactivate** the old web revisions.

**If a step fails:** traffic has not moved, unless the failure was in step 8 or later. The old
revision keeps serving.

**Rollback:**

```bash
az containerapp revision activate -g <rg> -n <app> --revision <old revision>
az containerapp ingress traffic set -g <rg> -n <app> --revision-weight <old revision>=100
az containerapp update -g <rg> -n <sync app> --image <previous sync image>
```

Migrations are forward-only in production. A rollback of code must remain compatible with the
newer schema, so write migrations expand-then-contract.

## 9. GitHub configuration

### Environments

| Environment | Protection | Used by |
|---|---|---|
| `<env>-plan` (`prod-plan`, `test-plan`) | Deployment branch: `main` | `build-images`, `what-if` |
| `<env>` (`prod`, `test`) | **Required reviewers**; deployment branch: `main` | `release` |

### Variables

Set these on both environments of a pair. Repository-level variables work where the value is the
same for every environment. None of them is a secret.

| Variable | Example | Notes |
|---|---|---|
| `AZURE_CLIENT_ID` | GUID | Deployment principal (OIDC) |
| `AZURE_TENANT_ID` | GUID | MLCP home tenant |
| `AZURE_SUBSCRIPTION_ID` | GUID | |
| `ACR_NAME` | `mlcpacr` | Registry name, without `.azurecr.io` |
| `ACR_RESOURCE_GROUP` | `mlcp-shared` | |
| `RESOURCE_GROUP_EU` / `RESOURCE_GROUP_US` | `mlcp-prod-eu` / `mlcp-prod-us` | |
| `ENTRA_CLIENT_ID` | GUID | Core registration |
| `USAGE_INSIGHTS_CLIENT_ID` | GUID | Usage Insights registration |
| `SQL_ADMIN_GROUP_OBJECT_ID` / `SQL_ADMIN_GROUP_NAME` | GUID / `MLCP SQL Admins (prod)` | |
| `PUBLIC_DOMAIN` | `example.com` | Hosts become `app.`, `eu.app.`, `us.app.` |
| `REGION_DIRECTORY_TABLE_URI` | `https://…table.core.windows.net/TenantRegions` | Global output |
| `REGION_DIRECTORY_STORAGE_ACCOUNT_ID` | `/subscriptions/…/storageAccounts/…` | Global output |
| `CUSTOMER_RBAC_TEMPLATE_URL` | `https://raw.githubusercontent.com/<owner>/<repo>/main/infra/customer-rbac.json` | Raw URL of the ARM JSON. Becomes `Mlcp:CustomerRbacTemplateUrl`. The URL must be publicly readable for the portal's *Deploy to Azure* button, so host the file in a public location if the repository is private |
| `EMAIL_ENDPOINT` / `EMAIL_SENDER_ADDRESS` | | Optional |

### Deployment principal (OIDC)

Use an app registration or a user-assigned identity with federated credentials for these subjects:
- `repo:<owner>/<repo>:environment:prod-plan`
- `repo:<owner>/<repo>:environment:prod`
- the same pair for `test`.

It needs these Azure permissions:

| Scope | Role | Why |
|---|---|---|
| Each regional resource group | **Contributor** | Deploy resources; start jobs; manage revisions and traffic |
| Each regional resource group | **Role Based Access Control Administrator** (ideally with a condition limited to the roles in the grants table) | Role assignments for the identities |
| Registry | **AcrPush** | Push images |
| Registry resource group | Deployment rights (`Microsoft.Resources/deployments/*`, e.g. Contributor) + **Role Based Access Control Administrator** on the registry, conditioned to `AcrPull` | The cross-resource-group `acr-pull` module |
| Global resource group | Deployment rights + **Role Based Access Control Administrator** on the storage account, conditioned to *Storage Table Data Contributor* | The `region-directory-access` module |

- The **migrator needs nothing from GitHub.** It is a managed identity inside the stack. The
  deployment principal never connects to SQL, and it is **not** a member of the SQL admin group.
- If `-plan` uses a separate, less-privileged principal, that principal needs:
  - AcrPush;
  - read access plus `Microsoft.Resources/deployments/whatIf/action` on the three resource groups
    (a custom role).

## Customer-side onboarding

Customers grant roles, in **their own** tenant, to the **MLCP enterprise application (service
principal)**. They never grant to any of the identities above.

- **Azure costs:** Guide B. The *Deploy to Azure* button deploys `infra/customer-rbac.json`, which
  assigns Cost Management Reader at a management group.
- **Prices and invoices:** Guide C (billing roles, granted by hand).
- **Usage reports:** Guide D (the separate Usage Insights consent).

Admin consent grants neither Azure RBAC nor billing roles. That is the most common onboarding
misunderstanding, and it is why the checklist shows each authorisation system separately.

## Operating

```bash
az containerapp logs show -g <rg> -n <prefix>-<env>-<region>-web --tail 50
az containerapp logs show -g <rg> -n <prefix>-<env>-<region>-sync --tail 50
az containerapp revision list -g <rg> -n <prefix>-<env>-<region>-web -o table
```

Within 15 minutes of starting, the sync worker logs a capability-discovery sweep. That shows the
worker identity, the database, Redis and Service Bus are all reachable.
