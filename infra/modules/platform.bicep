// Data and platform services: SQL, Azure Managed Redis, Service Bus, Key Vault, Storage, observability.
//
// Two rules apply throughout this file (ADR-026):
// - No passwords or access keys. SQL is Entra-only. Redis, Service Bus, Storage and Key Vault are
//   reached with managed identities, and Redis access keys are disabled.
// - Nothing is publicly reachable. Every service disables public network access and sits behind a
//   private endpoint.
//
// Web and worker have separate identities with different grants. The web identity cannot receive
// from the sync queue and has no system database role, so a compromised web process cannot read
// every tenant's data.

@description('Azure region for all resources.')
param location string

@description('Short environment name, e.g. prod or test.')
@minLength(2)
param environmentName string

@description('Region code, e.g. eu or us (ADR-021).')
@minLength(2)
param regionCode string

@description('Resource name prefix, e.g. mlcp.')
@minLength(3)
param namePrefix string

@description('Subnet to place private endpoints in.')
param privateEndpointSubnetId string

@description('Private DNS zone id for Azure SQL, from the network module.')
param sqlDnsZoneId string

@description('Private DNS zone id for Azure Managed Redis, from the network module.')
param redisDnsZoneId string

@description('Private DNS zone id for Service Bus, from the network module.')
param serviceBusDnsZoneId string

@description('Private DNS zone id for Key Vault, from the network module.')
param keyVaultDnsZoneId string

@description('Private DNS zone id for Blob storage, from the network module.')
param blobDnsZoneId string

@description('Principal (object) id of the web identity.')
param webPrincipalId string

@description('Principal (object) id of the worker identity.')
param workerPrincipalId string

@description('Entra object id of the group that administers the SQL server. The migrator identity must be a member.')
param sqlAdminGroupObjectId string

@description('Display name of that group.')
param sqlAdminGroupName string

@description('Backup storage redundancy for the database. Use Geo only where data may leave the region.')
@allowed([
  'Local'
  'Zone'
  'Geo'
  'GeoZone'
])
param requestedBackupStorageRedundancy string

@description('Azure Managed Redis SKU.')
param redisSkuName string

var baseName = '${namePrefix}-${environmentName}-${regionCode}'
var uniqueSuffix = uniqueString(resourceGroup().id)

// ------------------------------------------------------------------------------------------
// Observability
// ------------------------------------------------------------------------------------------
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2025-07-01' = {
  name: '${baseName}-logs'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 90
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${baseName}-insights'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

// ------------------------------------------------------------------------------------------
// Azure SQL. Entra-only authentication, so there is no SQL login and no password. Each workload
// connects as its own managed identity and maps to its own contained database user
// (infra/sql/create-users.sql).
// ------------------------------------------------------------------------------------------
resource sqlServer 'Microsoft.Sql/servers@2025-01-01' = {
  name: '${baseName}-sql'
  location: location
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Disabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'Group'
      login: sqlAdminGroupName
      sid: sqlAdminGroupObjectId
      tenantId: tenant().tenantId
      azureADOnlyAuthentication: true
    }
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2025-01-01' = {
  parent: sqlServer
  name: 'mlcp'
  location: location
  sku: {
    name: 'GP_S_Gen5'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: 2
  }
  properties: {
    // Serverless with auto-pause suits a load made mostly of scheduled sync jobs. Move to
    // provisioned compute once the tenant count justifies it.
    autoPauseDelay: 60
    minCapacity: json('0.5')
    zoneRedundant: false
    requestedBackupStorageRedundancy: requestedBackupStorageRedundancy
  }
}

resource sqlPrivateEndpoint 'Microsoft.Network/privateEndpoints@2025-07-01' = {
  name: '${baseName}-sql-pe'
  location: location
  properties: {
    subnet: {
      id: privateEndpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'sql'
        properties: {
          privateLinkServiceId: sqlServer.id
          groupIds: ['sqlServer']
        }
      }
    ]
  }
}

resource sqlDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2025-07-01' = {
  parent: sqlPrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'sql'
        properties: {
          privateDnsZoneId: sqlDnsZoneId
        }
      }
    ]
  }
}

// ------------------------------------------------------------------------------------------
// Azure Managed Redis: MSAL distributed token cache, consent nonces and read cache.
// Access keys are disabled. The apps authenticate with Entra ID via
// Microsoft.Azure.StackExchangeRedis, and only the identities with an access policy
// assignment below can connect.
// ------------------------------------------------------------------------------------------
resource redis 'Microsoft.Cache/redisEnterprise@2025-07-01' = {
  name: '${baseName}-redis'
  location: location
  sku: {
    name: redisSkuName
  }
  properties: {
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Disabled'
    highAvailability: 'Enabled'
  }
}

resource redisDatabase 'Microsoft.Cache/redisEnterprise/databases@2025-07-01' = {
  parent: redis
  name: 'default'
  properties: {
    clientProtocol: 'Encrypted'
    port: 10000
    clusteringPolicy: 'OSSCluster'
    evictionPolicy: 'VolatileLRU'
    accessKeysAuthentication: 'Disabled'
  }
}

resource redisWebAccess 'Microsoft.Cache/redisEnterprise/databases/accessPolicyAssignments@2025-07-01' = {
  parent: redisDatabase
  name: 'web'
  properties: {
    accessPolicyName: 'default'
    user: {
      objectId: webPrincipalId
    }
  }
}

resource redisWorkerAccess 'Microsoft.Cache/redisEnterprise/databases/accessPolicyAssignments@2025-07-01' = {
  parent: redisDatabase
  name: 'worker'
  properties: {
    accessPolicyName: 'default'
    user: {
      objectId: workerPrincipalId
    }
  }
  // Access policy assignments on one database are applied one at a time.
  dependsOn: [redisWebAccess]
}

resource redisPrivateEndpoint 'Microsoft.Network/privateEndpoints@2025-07-01' = {
  name: '${baseName}-redis-pe'
  location: location
  properties: {
    subnet: {
      id: privateEndpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'redis'
        properties: {
          privateLinkServiceId: redis.id
          groupIds: ['redisEnterprise']
        }
      }
    ]
  }
}

resource redisDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2025-07-01' = {
  parent: redisPrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'redis'
        properties: {
          privateDnsZoneId: redisDnsZoneId
        }
      }
    ]
  }
}

// ------------------------------------------------------------------------------------------
// Service Bus. Sessions give ordering within a tenant. Duplicate detection stops a scheduler that
// restarts mid-pass from double-running a tenant's sync (docs/03-architecture.md §6.1).
// ------------------------------------------------------------------------------------------
resource serviceBus 'Microsoft.ServiceBus/namespaces@2026-01-01' = {
  name: '${baseName}-sb'
  location: location
  sku: {
    // Premium is required for private endpoints. It also isolates throughput, so one large
    // tenant's sync cannot delay everyone else's.
    name: 'Premium'
    tier: 'Premium'
    capacity: 1
  }
  properties: {
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Disabled'
    disableLocalAuth: true
  }
}

resource syncQueue 'Microsoft.ServiceBus/namespaces/queues@2026-01-01' = {
  parent: serviceBus
  name: 'mlcp-sync-jobs'
  properties: {
    requiresSession: true
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    lockDuration: 'PT5M'
    maxDeliveryCount: 5
    defaultMessageTimeToLive: 'PT1H'
    deadLetteringOnMessageExpiration: true
  }
}

resource serviceBusPrivateEndpoint 'Microsoft.Network/privateEndpoints@2025-07-01' = {
  name: '${baseName}-sb-pe'
  location: location
  properties: {
    subnet: {
      id: privateEndpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'servicebus'
        properties: {
          privateLinkServiceId: serviceBus.id
          groupIds: ['namespace']
        }
      }
    ]
  }
}

resource serviceBusDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2025-07-01' = {
  parent: serviceBusPrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'servicebus'
        properties: {
          privateDnsZoneId: serviceBusDnsZoneId
        }
      }
    ]
  }
}

// ------------------------------------------------------------------------------------------
// Key Vault: the client certificate shared by both app registrations (ADR-015) and the Data
// Protection key. Uses RBAC rather than access policies, so its grants are audited like every
// other Azure permission.
// ------------------------------------------------------------------------------------------
resource keyVault 'Microsoft.KeyVault/vaults@2026-02-01' = {
  name: take('${namePrefix}${environmentName}${regionCode}kv${uniqueSuffix}', 24)
  location: location
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: tenant().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90

    // Purge protection cannot be turned off once enabled. That is intended: nobody can destroy
    // the keys that protect every tenant's session data, whether by attack or by mistake.
    enablePurgeProtection: true
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Deny'
    }
  }
}

resource keyVaultPrivateEndpoint 'Microsoft.Network/privateEndpoints@2025-07-01' = {
  name: '${baseName}-kv-pe'
  location: location
  properties: {
    subnet: {
      id: privateEndpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'keyvault'
        properties: {
          privateLinkServiceId: keyVault.id
          groupIds: ['vault']
        }
      }
    ]
  }
}

resource keyVaultDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2025-07-01' = {
  parent: keyVaultPrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'keyvault'
        properties: {
          privateDnsZoneId: keyVaultDnsZoneId
        }
      }
    ]
  }
}

// The Data Protection key wraps the key ring that protects auth cookies, the MSAL token cache and
// the admin-consent state, so it must outlive any single deployment.
resource dataProtectionKey 'Microsoft.KeyVault/vaults/keys@2026-02-01' = {
  parent: keyVault
  name: 'dataprotection'
  properties: {
    kty: 'RSA'
    keySize: 2048
    keyOps: ['wrapKey', 'unwrapKey']
  }
}

// ------------------------------------------------------------------------------------------
// Storage for the Data Protection key ring. Every instance shares it, so a restart or scale-out
// does not sign out every user.
// ------------------------------------------------------------------------------------------
resource storage 'Microsoft.Storage/storageAccounts@2025-06-01' = {
  name: take('${namePrefix}${environmentName}${regionCode}dp${uniqueSuffix}', 24)
  location: location
  sku: {
    name: 'Standard_ZRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Deny'
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2025-06-01' = {
  parent: storage
  name: 'default'
}

resource dataProtectionContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-06-01' = {
  parent: blobService
  name: 'dataprotection'
  properties: {
    publicAccess: 'None'
  }
}

resource storagePrivateEndpoint 'Microsoft.Network/privateEndpoints@2025-07-01' = {
  name: '${baseName}-blob-pe'
  location: location
  properties: {
    subnet: {
      id: privateEndpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'blob'
        properties: {
          privateLinkServiceId: storage.id
          groupIds: ['blob']
        }
      }
    ]
  }
}

resource storageDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2025-07-01' = {
  parent: storagePrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'blob'
        properties: {
          privateDnsZoneId: blobDnsZoneId
        }
      }
    ]
  }
}

// ------------------------------------------------------------------------------------------
// Role assignments (ADR-026). Least privilege: the apps read secrets and use the key; they do
// not manage the vault, the namespace or the storage account.
// ------------------------------------------------------------------------------------------
var roles = {
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  keyVaultCertificateUser: 'db79e9a7-68ee-4b58-9aeb-b90e7c24fcba'
  keyVaultCryptoUser: '12338af0-0e69-4776-bea7-57ae8d297424'
  storageBlobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  serviceBusDataSender: '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
  serviceBusDataReceiver: '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'
}

var workloads = [
  {
    name: 'web'
    principalId: webPrincipalId
  }
  {
    name: 'worker'
    principalId: workerPrincipalId
  }
]

// Both identities read the client certificate. Microsoft.Identity.Web and the token provider load
// it through its backing secret, so both Secrets User and Certificate User are needed.
resource keyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for w in workloads: {
    scope: keyVault
    name: guid(keyVault.id, w.principalId, roles.keyVaultSecretsUser)
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser)
      principalId: w.principalId
      principalType: 'ServicePrincipal'
      description: 'MLCP ${w.name}: read the client certificate secret'
    }
  }
]

resource keyVaultCertificateUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for w in workloads: {
    scope: keyVault
    name: guid(keyVault.id, w.principalId, roles.keyVaultCertificateUser)
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultCertificateUser)
      principalId: w.principalId
      principalType: 'ServicePrincipal'
      description: 'MLCP ${w.name}: read the client certificate'
    }
  }
]

// Only the web app protects cookies and the MSAL cache, so only it uses the Data Protection key.
resource dataProtectionKeyCryptoUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: dataProtectionKey
  name: guid(dataProtectionKey.id, webPrincipalId, roles.keyVaultCryptoUser)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultCryptoUser)
    principalId: webPrincipalId
    principalType: 'ServicePrincipal'
    description: 'MLCP web: wrap and unwrap the Data Protection key ring'
  }
}

resource dataProtectionBlobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: dataProtectionContainer
  name: guid(dataProtectionContainer.id, webPrincipalId, roles.storageBlobDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageBlobDataContributor)
    principalId: webPrincipalId
    principalType: 'ServicePrincipal'
    description: 'MLCP web: persist the Data Protection key ring'
  }
}

// Both identities enqueue: web queues discovery and verification jobs, worker queues follow-ups.
resource serviceBusSender 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for w in workloads: {
    scope: syncQueue
    name: guid(syncQueue.id, w.principalId, roles.serviceBusDataSender)
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataSender)
      principalId: w.principalId
      principalType: 'ServicePrincipal'
      description: 'MLCP ${w.name}: enqueue sync jobs'
    }
  }
]

// Only the worker receives.
resource serviceBusReceiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: syncQueue
  name: guid(syncQueue.id, workerPrincipalId, roles.serviceBusDataReceiver)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataReceiver)
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
    description: 'MLCP worker: consume sync jobs'
  }
}

output logAnalyticsName string = logAnalytics.name
output logAnalyticsCustomerId string = logAnalytics.properties.customerId
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlDatabaseName string = sqlDatabase.name

@description('Azure Managed Redis host name. Clients connect on port 10000 with TLS.')
output redisHostName string = redis.properties.hostName
output redisPort int = redisDatabase.properties.port
output serviceBusNamespaceFqdn string = '${serviceBus.name}.servicebus.windows.net'
output serviceBusQueueName string = syncQueue.name
output keyVaultUri string = keyVault.properties.vaultUri
output keyVaultName string = keyVault.name

@description('Versionless key URI, so a rotated key is used without a redeployment.')
output dataProtectionKeyUri string = dataProtectionKey.properties.keyUri
output dataProtectionBlobUri string = '${storage.properties.primaryEndpoints.blob}${dataProtectionContainer.name}'
output storageAccountName string = storage.name
