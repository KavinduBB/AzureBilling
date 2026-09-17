// Parameters for the EU regional stack. Deploy into resource group mlcp-<env>-eu in westeurope.
using 'main.bicep'

param regionCode = 'eu'
param location = 'westeurope'
param publicBaseUrl = 'https://eu.app.${readEnvironmentVariable('MLCP_PUBLIC_DOMAIN')}'

// Backups stay in the region: zone-redundant, not geo-replicated to a paired region.
param requestedBackupStorageRedundancy = 'Zone'
param redisSkuName = 'Balanced_B1'

// Deploy-time values come from the environment, so the same file serves the workflow and a manual
// run (infra/deploy.md). Client ids and object ids are configuration, not secrets.
param environmentName = readEnvironmentVariable('MLCP_ENVIRONMENT', 'prod')
param entraClientId = readEnvironmentVariable('MLCP_ENTRA_CLIENT_ID')
param usageInsightsClientId = readEnvironmentVariable('MLCP_USAGE_INSIGHTS_CLIENT_ID')
param sqlAdminGroupObjectId = readEnvironmentVariable('MLCP_SQL_ADMIN_GROUP_OBJECT_ID')
param sqlAdminGroupName = readEnvironmentVariable('MLCP_SQL_ADMIN_GROUP_NAME')
param containerRegistryName = readEnvironmentVariable('MLCP_ACR_NAME')
param containerRegistryResourceGroup = readEnvironmentVariable('MLCP_ACR_RESOURCE_GROUP')

param webImage = readEnvironmentVariable('MLCP_WEB_IMAGE')
param syncImage = readEnvironmentVariable('MLCP_SYNC_IMAGE')
param migrateImage = readEnvironmentVariable('MLCP_MIGRATE_IMAGE')
param webRevisionSuffix = readEnvironmentVariable('MLCP_WEB_REVISION_SUFFIX')
param webStableRevisionName = readEnvironmentVariable('MLCP_WEB_STABLE_REVISION', '')

// From the global deployment outputs (infra/global.bicep).
param regionDirectoryTableUri = readEnvironmentVariable('MLCP_REGION_DIRECTORY_TABLE_URI')
param regionDirectoryStorageAccountId = readEnvironmentVariable('MLCP_REGION_DIRECTORY_STORAGE_ACCOUNT_ID')

// Public host names (custom domains bound per infra/deploy.md).
var domain = readEnvironmentVariable('MLCP_PUBLIC_DOMAIN')
param globalHostName = 'app.${domain}'
param regionHosts = {
  eu: 'eu.app.${domain}'
  us: 'us.app.${domain}'
}
param customerRbacTemplateUrl = readEnvironmentVariable('MLCP_CUSTOMER_RBAC_TEMPLATE_URL')

param emailEndpoint = readEnvironmentVariable('MLCP_EMAIL_ENDPOINT', '')
param emailSenderAddress = readEnvironmentVariable('MLCP_EMAIL_SENDER_ADDRESS', '')
