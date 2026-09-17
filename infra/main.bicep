// MLCP: one regional stack (ADR-021, ADR-026).
//
//   az deployment group create \
//     --resource-group mlcp-prod-eu \
//     --template-file infra/main.bicep \
//     --parameters infra/main.eu.bicepparam \
//     --parameters webImage=... syncImage=... migrateImage=... webRevisionSuffix=... webStableRevisionName=...
//
// Each region (EU and US at launch) is an independent deployment with its own database, keys and
// identities. A tenant's region is chosen at connection and never migrated silently
// (docs/03-architecture.md §5.3). Global resources (Front Door and the tenant-to-region
// directory) come from infra/global.bicep, deployed once before any region.
// infra/deploy.md is the runbook.

targetScope = 'resourceGroup'

@description('Azure region for this stack.')
param location string = resourceGroup().location

@description('Region code stored on tenants that connect through this stack, and used in the global directory (ADR-021).')
@allowed([
  'eu'
  'us'
])
param regionCode string

@description('Short environment name, e.g. prod or test. Lower case: it is part of storage and Key Vault names.')
@minLength(2)
@maxLength(6)
param environmentName string

@description('Resource name prefix. Lower case letters only.')
@minLength(3)
@maxLength(6)
param namePrefix string = 'mlcp'

@description('Application (client) id of the core MLCP registration (ADR-015). Configuration, not a secret.')
param entraClientId string

@description('Application (client) id of the MLCP Usage Insights registration (ADR-015). Configuration, not a secret.')
param usageInsightsClientId string

@description('Name of the client certificate in Key Vault. Both registrations use it (infra/deploy.md).')
param clientCertificateName string = 'mlcp-client'

@description('Object id of the Entra group that administers the SQL server. Add the migrator identity to it (infra/deploy.md).')
param sqlAdminGroupObjectId string

@description('Display name of that group.')
param sqlAdminGroupName string

@description('SQL backup storage redundancy. Zone keeps backups in the region; Geo copies them to the paired region.')
@allowed([
  'Local'
  'Zone'
  'Geo'
  'GeoZone'
])
param requestedBackupStorageRedundancy string = 'Zone'

@description('Azure Managed Redis SKU.')
param redisSkuName string = 'Balanced_B1'

@description('Name of the shared container registry.')
param containerRegistryName string

@description('Resource group of the shared container registry.')
param containerRegistryResourceGroup string

@description('Web image reference including tag.')
param webImage string

@description('Sync worker image reference including tag. The deployment workflow passes the running image, then updates the worker after migrations.')
param syncImage string

@description('Migration bundle image reference including tag.')
param migrateImage string

@description('Suffix for the web revision this deployment creates, e.g. the image tag. Must be unique per deployment.')
@maxLength(20)
param webRevisionSuffix string

@description('Web revision that keeps all traffic while the new revision is verified. Leave empty on the first deployment.')
param webStableRevisionName string = ''

@description('Public base URL of this regional stack, e.g. https://eu.app.example.com.')
param publicBaseUrl string

@description('Host name of the global Front Door entry point, e.g. app.example.com.')
param globalHostName string

@description('Host name of every regional stack, keyed by region code.')
param regionHosts object

@description('TenantRegions table URI, from the global deployment output regionDirectoryTableUri.')
param regionDirectoryTableUri string

@description('Resource id of the global storage account, from the global deployment output regionDirectoryStorageAccountId.')
param regionDirectoryStorageAccountId string

@description('Raw URL of infra/customer-rbac.json for the Deploy to Azure button (docs/07 Guide B).')
param customerRbacTemplateUrl string

@description('Azure Communication Services endpoint for outbound email. Empty disables sending.')
param emailEndpoint string = ''

@description('Verified sender address for outbound email.')
param emailSenderAddress string = ''

var baseName = '${namePrefix}-${environmentName}-${regionCode}'

// Three user-assigned identities (ADR-026). They are internal to MLCP: customers grant roles to the
// MLCP enterprise application in their own tenant, never to these.
resource webIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: '${baseName}-web-identity'
  location: location
}

resource workerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: '${baseName}-worker-identity'
  location: location
}

resource migratorIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: '${baseName}-migrator-identity'
  location: location
}

module network 'modules/network.bicep' = {
  name: 'network'
  params: {
    location: location
    environmentName: '${environmentName}-${regionCode}'
    namePrefix: namePrefix
  }
}

module platform 'modules/platform.bicep' = {
  name: 'platform'
  params: {
    location: location
    environmentName: environmentName
    regionCode: regionCode
    namePrefix: namePrefix
    privateEndpointSubnetId: network.outputs.privateEndpointSubnetId
    sqlDnsZoneId: network.outputs.sqlDnsZoneId
    redisDnsZoneId: network.outputs.redisDnsZoneId
    serviceBusDnsZoneId: network.outputs.serviceBusDnsZoneId
    keyVaultDnsZoneId: network.outputs.keyVaultDnsZoneId
    blobDnsZoneId: network.outputs.blobDnsZoneId
    webPrincipalId: webIdentity.properties.principalId
    workerPrincipalId: workerIdentity.properties.principalId
    sqlAdminGroupObjectId: sqlAdminGroupObjectId
    sqlAdminGroupName: sqlAdminGroupName
    requestedBackupStorageRedundancy: requestedBackupStorageRedundancy
    redisSkuName: redisSkuName
  }
}

module acrPull 'modules/acr-pull.bicep' = {
  name: 'acr-pull-${baseName}'
  scope: resourceGroup(containerRegistryResourceGroup)
  params: {
    containerRegistryName: containerRegistryName
    principalIds: [
      webIdentity.properties.principalId
      workerIdentity.properties.principalId
      migratorIdentity.properties.principalId
    ]
  }
}

// The global storage account may be in another subscription and resource group, so its id is
// split to scope the module.
var directoryIdParts = split(regionDirectoryStorageAccountId, '/')

module regionDirectoryAccess 'modules/region-directory-access.bicep' = {
  name: 'region-directory-${baseName}'
  scope: resourceGroup(directoryIdParts[2], directoryIdParts[4])
  params: {
    storageAccountName: last(directoryIdParts)
    principalIds: [
      webIdentity.properties.principalId
      workerIdentity.properties.principalId
    ]
  }
}

// Log Analytics still issues a shared key and cannot accept a federated identity, so the key is
// read back here. It is the only credential this template handles.
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2025-07-01' existing = {
  name: '${baseName}-logs'
  dependsOn: [platform]
}

module apps 'modules/apps.bicep' = {
  name: 'apps'
  params: {
    location: location
    baseName: baseName
    regionCode: regionCode
    appsSubnetId: network.outputs.appsSubnetId
    webIdentityId: webIdentity.id
    webIdentityClientId: webIdentity.properties.clientId
    workerIdentityId: workerIdentity.id
    workerIdentityClientId: workerIdentity.properties.clientId
    migratorIdentityId: migratorIdentity.id
    migratorIdentityClientId: migratorIdentity.properties.clientId
    logAnalyticsCustomerId: platform.outputs.logAnalyticsCustomerId
    logAnalyticsSharedKey: logAnalytics.listKeys().primarySharedKey
    appInsightsConnectionString: platform.outputs.appInsightsConnectionString
    containerRegistryServer: acrPull.outputs.loginServer
    webImage: webImage
    syncImage: syncImage
    migrateImage: migrateImage
    webRevisionSuffix: webRevisionSuffix
    webStableRevisionName: webStableRevisionName
    entraClientId: entraClientId
    usageInsightsClientId: usageInsightsClientId
    clientCertificateName: clientCertificateName
    keyVaultUri: platform.outputs.keyVaultUri
    dataProtectionKeyUri: platform.outputs.dataProtectionKeyUri
    dataProtectionBlobUri: platform.outputs.dataProtectionBlobUri
    serviceBusNamespaceFqdn: platform.outputs.serviceBusNamespaceFqdn
    sqlServerFqdn: platform.outputs.sqlServerFqdn
    sqlDatabaseName: platform.outputs.sqlDatabaseName
    redisHostName: platform.outputs.redisHostName
    redisPort: platform.outputs.redisPort
    publicBaseUrl: publicBaseUrl
    globalHostName: globalHostName
    regionHosts: regionHosts
    regionDirectoryTableUri: regionDirectoryTableUri
    customerRbacTemplateUrl: customerRbacTemplateUrl
    emailEndpoint: emailEndpoint
    emailSenderAddress: emailSenderAddress
  }
  // Role assignments must exist before the first revision pulls its image and reads the table.
  dependsOn: [
    regionDirectoryAccess
  ]
}

@description('Default Container Apps host name of the web app. Use it as this region\'s Front Door origin until a custom domain is bound.')
output webFqdn string = apps.outputs.webFqdn

@description('Container Apps environment default domain. A revision FQDN is <webAppName>--<revisionSuffix>.<this domain>.')
output environmentDefaultDomain string = apps.outputs.environmentDefaultDomain

@description('Redirect URIs to register on the core app registration for this region (infra/entra.md).')
output entraRedirectUris string[] = [
  '${publicBaseUrl}/signin-oidc'
  '${publicBaseUrl}/onboarding/consent-callback'
]

@description('Principal id of the migrator identity. Add it to the SQL admin group before the first migrate job runs. Customers never grant anything to MLCP\'s managed identities; they grant roles to the MLCP enterprise application (service principal) in their own tenant.')
output migratorPrincipalId string = migratorIdentity.properties.principalId

output webIdentityName string = webIdentity.name
output workerIdentityName string = workerIdentity.name

@description('Key Vault that holds the client certificate.')
output keyVaultName string = platform.outputs.keyVaultName

output sqlServerFqdn string = platform.outputs.sqlServerFqdn
output webAppName string = apps.outputs.webAppName
output syncAppName string = apps.outputs.syncAppName
output migrateJobName string = apps.outputs.migrateJobName
output createUsersJobName string = apps.outputs.createUsersJobName
