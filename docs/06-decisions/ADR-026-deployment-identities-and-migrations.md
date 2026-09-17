# ADR-026 — Separate web/worker identities, in-VNet migrations before rollout, Azure Managed Redis
Status: Accepted · Date: 2026-09-17 · Amends: infra/*, docs/03 §8, ADR-003

## Context

The deployment review found five problems:

- **Shared identity.** Web and worker share one managed identity and one database user. The RLS "system" bypass is a session flag any code on that connection could set, so a compromised web process could read every tenant.
- **Migrations can't reach SQL.** The `migrate` job runs on a GitHub-hosted runner, but SQL and Key Vault have public access disabled. It also runs *after* the new image is rolled out.
- **First deploy fails.** The first deployment cannot pull images, because nothing grants `AcrPull`.
- **Sign-in has no credential.** The web app has no client credential in Azure, so code redemption at sign-in fails.
- **Redis creation may be blocked.** Azure Cache for Redis blocks new instances for existing customers from 1 Oct 2026 ([retirement FAQ](https://learn.microsoft.com/en-us/azure/azure-cache-for-redis/retirement-faq)).

## Decision

**Identities and least privilege**

- Two user-assigned identities: `…-web-identity` and `…-worker-identity`. Each maps to its own contained database user:
  - `mlcp_web_user`, in role `mlcp_web`;
  - `mlcp_worker_user`, in roles `mlcp_worker` and `mlcp_system`.
- The RLS system clause becomes:
  ```sql
  (CAST(SESSION_CONTEXT(N'IsSystem') AS bit) = 1 AND IS_MEMBER(N'mlcp_system') = 1)
  ```
  A web connection that sets the flag still sees nothing.
- **Service Bus:** web gets **Data Sender** (it enqueues discovery); worker gets **Data Receiver** and **Data Sender**. Data Owner is removed.
- **Key Vault:** both identities get Secrets User and Certificate User. Crypto User on the Data Protection key is web only.
- **Storage:** Blob Data Contributor on the Data Protection container is web only. *Storage Table Data Contributor* on the global region table goes to both (ADR-021).
- **Workload database users** get no DDL rights.

**Migrations run inside the VNet, before traffic moves**

1. CI builds an **EF Core migration bundle** (`dotnet ef migrations bundle --self-contained -r linux-x64`) into a `mlcp-migrate` image.
2. `infra/modules/apps.bicep` defines a manually triggered **Container Apps Job** `…-migrate`. It runs in the same VNet-integrated environment, as a third identity `…-migrator-identity`, which is a member of the SQL admin Entra group.
3. The deployment workflow runs, in order:
   1. Build and push images.
   2. `what-if`.
   3. Deploy infrastructure, with the new revisions created at **0% traffic** (`activeRevisionsMode: Multiple`).
   4. Start the migrate job and wait for success.
   5. Smoke-test the new revision label.
   6. Shift traffic to 100%.
   7. Deactivate the old revision.
4. `/health/ready` checks SQL (`AddDbContextCheck`), Redis and Key Vault reachability. `/health/live` is process-only. The smoke test uses `/health/ready` on the revision-specific FQDN.
5. Operator data-plane steps (certificate creation, SQL user creation) are scripted as the same migrate job (`--create-users` mode, using `CREATE USER … FROM EXTERNAL PROVIDER` with the identities' names). No workstation access to private endpoints is needed. Key Vault certificate creation uses the control-plane-authorised `az keyvault certificate create`, run from the job's image, or via a temporary IP allow rule documented in `infra/deploy.md`.

**Other fixes**

- **AcrPull.** Granted in Bicep to the web, worker and migrator identities, through a module scoped to the registry's resource group. The apps module depends on it.
- **Sign-in credential.**
  - The web app sets `AzureAd__ClientCredentials__0__SourceType=KeyVault`, plus the Key Vault URL, certificate name, and `AzureAd__ClientCredentials__0__ManagedIdentityClientId` (the web identity).
  - Token acquisition is enabled only for sign-in code redemption; no downstream scopes in Phase 0.
  - The MSAL distributed cache is encrypted (`MsalDistributedTokenCacheAdapterOptions.Encrypt = true`), with Data Protection keys in Key Vault (rule 3).
- **Redis.** Moves to **Azure Managed Redis** (`Microsoft.Cache/redisEnterprise`) with Entra (managed identity) authentication through `Microsoft.Azure.StackExchangeRedis`, so no access keys exist. Local development still uses the Redis container.
- **Forwarded headers.** `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` is set on the web app, and `UseForwardedHeaders` runs first in the pipeline, so the scheme and host from the Container Apps ingress are honoured.
- **Worker hosting.** The worker stays a long-running Container App (min = max = 1 per region, Service Bus sessions for ordering). **Container Apps Jobs** are used for migrations and for a KEDA-scaled overflow worker only if queue depth demands it. This deviation from the `CLAUDE.md` stack line is recorded here.

## Consequences

+ The web tier can no longer bypass RLS or read other tenants' queues.
+ Schema always leads code.
+ Deployments work with private endpoints on.
− Three identities and one more job to operate. `infra/deploy.md` is the runbook.
