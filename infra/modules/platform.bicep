// Data and platform services: SQL, Redis, Service Bus, Key Vault, Storage, observability.
//
// Two themes run through this file. First, no passwords: SQL is Entra-only, Service Bus, Storage
// and Key Vault are reached with the workload's managed identity, and the only secret anywhere is
// the Redis access key, which Redis still requires (CLAUDE.md rule 4). Second, nothing is
// publicly reachable: every service disables public network access and is fronted by a private
// endpoint.

@description('Azure region for all resources.')
param location string

@description('Short environment name, e.g. prod or test.')
param environmentName string

@description('Resource name prefix, e.g. mlcp.')
param namePrefix string

@description('Subnet to place private endpoints in.')
param privateEndpointSubnetId string

@description('Private DNS zone ids, from the network module.')
param sqlDnsZoneId string
param redisDnsZoneId string
param serviceBusDnsZoneId string
param keyVaultDnsZoneId string
param blobDnsZoneId string

@description('Principal id of the workload user-assigned managed identity.')
param workloadPrincipalId string

@description('Entra object id of the group that administers the SQL server.')
param sqlAdminGroupObjectId string

@description('Display name of that group.')
param sqlAdminGroupName string

var baseName = '${namePrefix}-${environmentName}'
var uniqueSuffix = uniqueString(resourceGroup().id)

// ------------------------------------------------------------------------------------------
// Observability
// ------------------------------------------------------------------------------------------
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
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
// Azure SQL. Entra-only authentication: there is no SQL login and therefore no password to
// leak, rotate or find in a config file. The apps connect as their managed identity.
// ------------------------------------------------------------------------------------------
resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
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

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
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
    // Serverless with auto-pause suits a platform whose load is sync jobs on a schedule rather
    // than constant traffic. Raise or move to provisioned once tenant count justifies it.
    autoPauseDelay: 60
    minCapacity: json('0.5')
    zoneRedundant: false
    requestedBackupStorageRedundancy: 'Zone'
  }
}

resource sqlPrivateEndpoint 'Microsoft.Network/privateEndpoints@2024-01-01' = {
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

resource sqlDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-01-01' = {
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
// Redis: MSAL distributed token cache and read cache.
// ------------------------------------------------------------------------------------------
resource redis 'Microsoft.Cache/redis@2024-03-01' = {
  name: '${baseName}-redis'
  location: location
  properties: {
    sku: {
      name: 'Standard'
      family: 'C'
      capacity: 1
    }
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Disabled'
    redisConfiguration: {
      'maxmemory-policy': 'volatile-lru'
    }
  }
}

resource redisPrivateEndpoint 'Microsoft.Network/privateEndpoints@2024-01-01' = {
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
          groupIds: ['redisCache']
        }
      }
    ]
  }
}

resource redisDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-01-01' = {
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
// Service Bus. Sessions give ordering within a tenant; duplicate detection means a scheduler
// that restarts mid-pass cannot double-run a tenant's sync (docs/03-architecture.md §6.1).
// ------------------------------------------------------------------------------------------
resource serviceBus 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: '${baseName}-sb'
  location: location
  sku: {
    name: 'Premium'
    tier: 'Premium'
    capacity: 1
  }
  properties: {
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Disabled'

    // Premium is required for private endpoints. It also removes the shared-throughput noisy
    // neighbour risk that would otherwise let one large tenant's sync delay everyone else's.
    disableLocalAuth: true
  }
}

resource syncQueue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = {
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

resource serviceBusPrivateEndpoint 'Microsoft.Network/privateEndpoints@2024-01-01' = {
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

resource serviceBusDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-01-01' = {
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
// Key Vault: the multi-tenant app's client certificate, the Data Protection key, and any
// remaining connection secrets. RBAC rather than access policies, so grants are visible in the
// same place as every other Azure permission and can be audited the same way.
// ------------------------------------------------------------------------------------------
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: '${namePrefix}${environmentName}kv${take(uniqueSuffix, 6)}'
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

    // Purge protection cannot be turned off once on. That is the point: it stops an attacker,
    // or a mistake, from destroying the keys that protect every tenant's session data.
    enablePurgeProtection: true
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Deny'
    }
  }
}

resource keyVaultPrivateEndpoint 'Microsoft.Network/privateEndpoints@2024-01-01' = {
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

resource keyVaultDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-01-01' = {
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

// The Redis connection string, including its access key. Kept in Key Vault rather than passed
// to the apps module as a parameter: a Container Apps secret with an inline value is recorded in
// the ARM deployment payload, where it is readable by anyone with deployment-history access
// long after the key is rotated. The app resolves this at runtime with its managed identity, so
// rotating the key means updating one secret and nothing else.
resource redisConnectionSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'redis-connection'
  properties: {
    value: '${redis.properties.hostName}:6380,password=${redis.listKeys().primaryKey},ssl=True,abortConnect=False'
  }
}

// The Data Protection key. Encrypts the keys that in turn protect auth cookies, the MSAL token
// cache and the admin-consent state, so it must outlive any single deployment.
resource dataProtectionKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' = {
  parent: keyVault
  name: 'dataprotection'
  properties: {
    kty: 'RSA'
    keySize: 2048
    keyOps: ['wrapKey', 'unwrapKey']
  }
}

// ------------------------------------------------------------------------------------------
// Storage for the Data Protection key ring. Shared across instances so a restart or scale-out
// does not invalidate every signed-in session.
// ------------------------------------------------------------------------------------------
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: '${namePrefix}${environmentName}dp${take(uniqueSuffix, 8)}'
  location: location
  sku: {
    name: 'Standard_ZRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Deny'
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource dataProtectionContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'dataprotection'
  properties: {
    publicAccess: 'None'
  }
}

resource storagePrivateEndpoint 'Microsoft.Network/privateEndpoints@2024-01-01' = {
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

resource storageDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-01-01' = {
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
// Role assignments for the workload identity. Least privilege throughout: the app reads
// secrets and uses the key, it does not manage the vault.
// ------------------------------------------------------------------------------------------
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'
var keyVaultCryptoUserRoleId = '12338af0-0e69-4776-bea7-57ae8d297424'
var keyVaultCertificateUserRoleId = 'db79e9a7-68ee-4b58-9aeb-b90e7c24fcba'
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var serviceBusDataOwnerRoleId = '090c5cfd-751d-490a-894a-3ce6f1109419'

resource keyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, workloadPrincipalId, keyVaultSecretsUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: workloadPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource keyVaultCryptoUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, workloadPrincipalId, keyVaultCryptoUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultCryptoUserRoleId)
    principalId: workloadPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource keyVaultCertificateUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, workloadPrincipalId, keyVaultCertificateUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultCertificateUserRoleId)
    principalId: workloadPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource storageBlobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, workloadPrincipalId, storageBlobDataContributorRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: workloadPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource serviceBusDataOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: serviceBus
  name: guid(serviceBus.id, workloadPrincipalId, serviceBusDataOwnerRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataOwnerRoleId)
    principalId: workloadPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output logAnalyticsId string = logAnalytics.id
output logAnalyticsCustomerId string = logAnalytics.properties.customerId
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlDatabaseName string = sqlDatabase.name
output redisHostName string = redis.properties.hostName
// Versionless on purpose: Container Apps re-reads it, so a rotated key takes effect
// without redeploying the application.
output redisConnectionSecretUri string = '${keyVault.properties.vaultUri}secrets/redis-connection'
output serviceBusNamespaceFqdn string = '${serviceBus.name}.servicebus.windows.net'
output serviceBusQueueName string = syncQueue.name
output keyVaultUri string = keyVault.properties.vaultUri
output keyVaultName string = keyVault.name
output dataProtectionKeyUri string = dataProtectionKey.properties.keyUriWithVersion
output dataProtectionBlobUri string = '${storage.properties.primaryEndpoints.blob}${dataProtectionContainer.name}'
output storageAccountName string = storage.name
