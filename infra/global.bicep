// MLCP: global resources, deployed once before any regional stack (ADR-021).
//
//   az group create -n mlcp-prod-global -l westeurope
//   az deployment group create -g mlcp-prod-global \
//     --template-file infra/global.bicep --parameters infra/global.bicepparam
//
// Two resources are global:
// - The tenant-to-region directory: a geo-redundant storage table holding only
//   (tid, region code, registered timestamp).
// - Azure Front Door, the one public entry point (app.<domain>), plus one endpoint per region
//   (eu.app.<domain>, us.app.<domain>).
//
// Custom domains and their DNS validation are bound after this deployment (infra/deploy.md),
// because they need DNS records that Bicep cannot create in an external DNS provider.

targetScope = 'resourceGroup'

@description('Location for the storage account. Front Door itself is global.')
param location string = resourceGroup().location

@description('Short environment name, e.g. prod or test. Lower case.')
@minLength(2)
@maxLength(6)
param environmentName string

@description('Resource name prefix. Lower case letters only.')
@minLength(3)
@maxLength(6)
param namePrefix string = 'mlcp'

@description('Regional web origins: code (eu/us) and the host name Front Door forwards to, which is the regional web app\'s Container Apps FQDN (main.bicep output webFqdn).')
param regionalOrigins {
  code: string
  hostName: string
}[]

@description('Region whose origin serves the global endpoint when latency is equal. Landing pages are served by any region.')
param defaultRegionCode string = 'eu'

@description('Front Door SKU. Premium adds Private Link origins and managed WAF rule sets.')
@allowed([
  'Standard_AzureFrontDoor'
  'Premium_AzureFrontDoor'
])
param frontDoorSku string = 'Standard_AzureFrontDoor'

var baseName = '${namePrefix}-${environmentName}-global'
var tableName = 'TenantRegions'

// ------------------------------------------------------------------------------------------
// Tenant-to-region directory. Read-access geo-zone-redundant storage, Entra auth only.
// Shared keys are disabled, so the only way in is an RBAC grant on the table
// (modules/region-directory-access.bicep, deployed from each regional stack).
//
// The table is reached over its public endpoint. It holds no customer data (tenant GUID, region
// code, timestamp) and every request needs an Entra token with a table-scoped role.
// ------------------------------------------------------------------------------------------
resource directoryStorage 'Microsoft.Storage/storageAccounts@2025-06-01' = {
  name: take('${namePrefix}${environmentName}dir${uniqueString(resourceGroup().id)}', 24)
  location: location
  sku: {
    name: 'Standard_RAGZRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
  }

  resource tableService 'tableServices' = {
    name: 'default'

    resource table 'tables' = {
      name: tableName
    }
  }
}

// ------------------------------------------------------------------------------------------
// Front Door. Its origins are the regional web apps, which do not exist on the first run, so it is
// deployed only when regionalOrigins is non-empty (infra/deploy.md, "First deployment").
// ------------------------------------------------------------------------------------------
module frontDoor 'modules/front-door.bicep' = if (!empty(regionalOrigins)) {
  name: 'front-door'
  params: {
    baseName: baseName
    endpointPrefix: '${namePrefix}-${environmentName}'
    regionalOrigins: regionalOrigins
    defaultRegionCode: defaultRegionCode
    frontDoorSku: frontDoorSku
  }
}

@description('Pass to each regional deployment as regionDirectoryTableUri.')
output regionDirectoryTableUri string = '${directoryStorage.properties.primaryEndpoints.table}${tableName}'

@description('Pass to each regional deployment as regionDirectoryStorageAccountId.')
output regionDirectoryStorageAccountId string = directoryStorage.id

@description('Front Door host name of the global entry point. Bind app.<domain> to it. Empty until regionalOrigins is supplied.')
output frontDoorHostName string = empty(regionalOrigins) ? '' : frontDoor!.outputs.globalHostName

@description('Front Door host names of the regional endpoints ({ code, hostName }). Bind <code>.app.<domain> to each.')
output regionalFrontDoorHostNames array = empty(regionalOrigins) ? [] : frontDoor!.outputs.regionalHostNames
