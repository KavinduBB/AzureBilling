// Virtual network, subnets and private DNS for MLCP.
//
// Every data service is reached over a Private Endpoint and has public network access disabled
// (docs/03-architecture.md §8). That is the control that makes a leaked connection string
// useless from outside the network, which matters more here than usual: the platform holds
// licence and spend data for many organisations in one database.

@description('Azure region for all resources.')
param location string

@description('Short environment name, e.g. prod or test.')
param environmentName string

@description('Resource name prefix, e.g. mlcp.')
param namePrefix string

@description('Address space for the virtual network.')
param vnetAddressPrefix string = '10.40.0.0/16'

@description('Subnet delegated to the Container Apps environment. Must be at least /23.')
param appsSubnetPrefix string = '10.40.0.0/23'

@description('Subnet holding the private endpoints for data services.')
param privateEndpointSubnetPrefix string = '10.40.2.0/24'

var vnetName = '${namePrefix}-${environmentName}-vnet'

// Private DNS zones. Without these the private endpoint exists but the service's public
// hostname still resolves to its public IP, so the app would connect over the internet or not
// at all, depending on the firewall. Each zone overrides that resolution inside the VNet.
var privateDnsZoneNames = [
  'privatelink${environment().suffixes.sqlServerHostname}'
  // Azure Managed Redis (Microsoft.Cache/redisEnterprise), not the retired Azure Cache for Redis
  // zone (ADR-026).
  'privatelink.redis.azure.net'
  'privatelink.servicebus.windows.net'
  'privatelink.vaultcore.azure.net'
  'privatelink.blob.${environment().suffixes.storage}'
]

resource vnet 'Microsoft.Network/virtualNetworks@2025-07-01' = {
  name: vnetName
  location: location
  properties: {
    addressSpace: {
      addressPrefixes: [vnetAddressPrefix]
    }
    subnets: [
      {
        name: 'apps'
        properties: {
          addressPrefix: appsSubnetPrefix
          delegations: [
            {
              name: 'containerapps'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
        }
      }
      {
        name: 'private-endpoints'
        properties: {
          addressPrefix: privateEndpointSubnetPrefix

          // Required so the platform can place private endpoint NICs here.
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
    ]
  }
}

resource privateDnsZones 'Microsoft.Network/privateDnsZones@2024-06-01' = [
  for zoneName in privateDnsZoneNames: {
    name: zoneName
    location: 'global'
  }
]

resource privateDnsZoneLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = [
  for (zoneName, i) in privateDnsZoneNames: {
    name: '${zoneName}/${vnetName}-link'
    location: 'global'
    dependsOn: [privateDnsZones[i]]
    properties: {
      registrationEnabled: false
      virtualNetwork: {
        id: vnet.id
      }
    }
  }
]

output vnetId string = vnet.id
output appsSubnetId string = vnet.properties.subnets[0].id
output privateEndpointSubnetId string = vnet.properties.subnets[1].id
output sqlDnsZoneId string = resourceId('Microsoft.Network/privateDnsZones', privateDnsZoneNames[0])
output redisDnsZoneId string = resourceId('Microsoft.Network/privateDnsZones', privateDnsZoneNames[1])
output serviceBusDnsZoneId string = resourceId('Microsoft.Network/privateDnsZones', privateDnsZoneNames[2])
output keyVaultDnsZoneId string = resourceId('Microsoft.Network/privateDnsZones', privateDnsZoneNames[3])
output blobDnsZoneId string = resourceId('Microsoft.Network/privateDnsZones', privateDnsZoneNames[4])
