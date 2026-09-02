// MLCP — one region, one deployment.
//
//   az deployment group create \
//     --resource-group mlcp-prod-weu \
//     --template-file infra/main.bicep \
//     --parameters infra/main.prod.bicepparam
//
// The platform deploys once per region (docs/03-architecture.md §5.3). A tenant's region is
// chosen at connection and never migrated silently, so each region is an independent stack with
// its own database, its own keys and its own identity.

targetScope = 'resourceGroup'

@description('Azure region. Also recorded on tenants that connect through this deployment.')
param location string = resourceGroup().location

@description('Short environment name, e.g. prod or test.')
@minLength(2)
@maxLength(8)
param environmentName string

@description('Resource name prefix.')
@minLength(3)
@maxLength(8)
param namePrefix string = 'mlcp'

@description('Entra application (client) id of the multi-tenant MLCP registration.')
param entraClientId string

@description('Name of the client certificate in Key Vault, uploaded after the vault exists.')
param clientCertificateName string = 'mlcp-client'

@description('Object id of the Entra group that administers the SQL server.')
param sqlAdminGroupObjectId string

@description('Display name of that group.')
param sqlAdminGroupName string

@description('Container registry login server, e.g. mlcpacr.azurecr.io.')
param containerRegistryServer string

@description('Web image reference including tag.')
param webImage string

@description('Sync worker image reference including tag.')
param syncImage string

@description('Azure Communication Services endpoint for outbound email. Empty disables sending.')
param emailEndpoint string = ''

@description('Verified sender address for outbound email.')
param emailSenderAddress string = ''

var baseName = '${namePrefix}-${environmentName}'

// One identity for both workloads. User-assigned rather than system-assigned so it survives an
// app being recreated: customers grant Azure and billing roles to this principal in their own
// tenants, and those grants must not be invalidated by a redeployment on our side.
resource workloadIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${baseName}-identity'
  location: location
}

module network 'modules/network.bicep' = {
  name: 'network'
  params: {
    location: location
    environmentName: environmentName
    namePrefix: namePrefix
  }
}

module platform 'modules/platform.bicep' = {
  name: 'platform'
  params: {
    location: location
    environmentName: environmentName
    namePrefix: namePrefix
    privateEndpointSubnetId: network.outputs.privateEndpointSubnetId
    sqlDnsZoneId: network.outputs.sqlDnsZoneId
    redisDnsZoneId: network.outputs.redisDnsZoneId
    serviceBusDnsZoneId: network.outputs.serviceBusDnsZoneId
    keyVaultDnsZoneId: network.outputs.keyVaultDnsZoneId
    blobDnsZoneId: network.outputs.blobDnsZoneId
    workloadPrincipalId: workloadIdentity.properties.principalId
    sqlAdminGroupObjectId: sqlAdminGroupObjectId
    sqlAdminGroupName: sqlAdminGroupName
  }
}

// Log Analytics still issues a shared key rather than accepting a federated identity, so it is
// read back here. It is the only credential this template handles directly; the Redis key is
// written straight into Key Vault by the platform module and never passes through here.
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: '${baseName}-logs'
}

module apps 'modules/apps.bicep' = {
  name: 'apps'
  params: {
    location: location
    environmentName: environmentName
    namePrefix: namePrefix
    appsSubnetId: network.outputs.appsSubnetId
    workloadIdentityId: workloadIdentity.id
    workloadIdentityClientId: workloadIdentity.properties.clientId
    logAnalyticsCustomerId: platform.outputs.logAnalyticsCustomerId
    logAnalyticsSharedKey: logAnalytics.listKeys().primarySharedKey
    appInsightsConnectionString: platform.outputs.appInsightsConnectionString
    containerRegistryServer: containerRegistryServer
    webImage: webImage
    syncImage: syncImage
    entraClientId: entraClientId
    clientCertificateName: clientCertificateName
    keyVaultUri: platform.outputs.keyVaultUri
    dataProtectionKeyUri: platform.outputs.dataProtectionKeyUri
    dataProtectionBlobUri: platform.outputs.dataProtectionBlobUri
    serviceBusNamespaceFqdn: platform.outputs.serviceBusNamespaceFqdn
    sqlServerFqdn: platform.outputs.sqlServerFqdn
    sqlDatabaseName: platform.outputs.sqlDatabaseName
    redisConnectionSecretUri: platform.outputs.redisConnectionSecretUri
    tenantRegion: location
    emailEndpoint: emailEndpoint
    emailSenderAddress: emailSenderAddress
  }
}

@description('Public hostname of the web application. Use it for the Entra redirect URI.')
output webFqdn string = apps.outputs.webFqdn

@description('Redirect URI to register in Entra.')
output entraRedirectUri string = 'https://${apps.outputs.webFqdn}/signin-oidc'

@description('Object id customers grant Azure and billing roles to.')
output workloadPrincipalId string = workloadIdentity.properties.principalId

@description('Key Vault the client certificate must be uploaded to.')
output keyVaultName string = platform.outputs.keyVaultName

@description('SQL server to create the contained database user in.')
output sqlServerFqdn string = platform.outputs.sqlServerFqdn

output webAppName string = apps.outputs.webAppName
output syncAppName string = apps.outputs.syncAppName
