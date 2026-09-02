// Container Apps environment, the web application, and the sync worker.
//
// Both workloads run as the same user-assigned managed identity. That is deliberate: they call
// the same Microsoft APIs on behalf of the same multi-tenant application registration, so
// splitting them would mean two identities to grant, two to rotate, and two for a customer to
// recognise in their own directory.

@description('Azure region for all resources.')
param location string

@description('Short environment name, e.g. prod or test.')
param environmentName string

@description('Resource name prefix, e.g. mlcp.')
param namePrefix string

@description('Subnet delegated to the Container Apps environment.')
param appsSubnetId string

@description('Resource id of the workload user-assigned managed identity.')
param workloadIdentityId string

@description('Client id of that identity, passed to the apps so DefaultAzureCredential picks it.')
param workloadIdentityClientId string

@description('Log Analytics workspace id for container logs.')
param logAnalyticsCustomerId string

@secure()
@description('Log Analytics shared key.')
param logAnalyticsSharedKey string

@description('Application Insights connection string.')
param appInsightsConnectionString string

@description('Container registry login server, e.g. mlcpacr.azurecr.io.')
param containerRegistryServer string

@description('Web image reference, including tag.')
param webImage string

@description('Sync worker image reference, including tag.')
param syncImage string

@description('Entra application (client) id of the multi-tenant registration.')
param entraClientId string

@description('Name of the client certificate in Key Vault.')
param clientCertificateName string

@description('Key Vault URI.')
param keyVaultUri string

@description('Data Protection key URI, including version.')
param dataProtectionKeyUri string

@description('Data Protection blob container URI.')
param dataProtectionBlobUri string

@description('Service Bus fully qualified namespace.')
param serviceBusNamespaceFqdn string

@description('SQL server fully qualified domain name.')
param sqlServerFqdn string

@description('SQL database name.')
param sqlDatabaseName string

@description('Key Vault URI of the Redis connection string secret.')
param redisConnectionSecretUri string

@description('Azure region name recorded on tenants connected through this deployment.')
param tenantRegion string

@description('Azure Communication Services endpoint for outbound email.')
param emailEndpoint string = ''

@description('Verified sender address for outbound email.')
param emailSenderAddress string = ''

var baseName = '${namePrefix}-${environmentName}'

// Entra-only SQL: no password anywhere. The workload authenticates as its managed identity, so
// the connection string is not a secret and can sit in plain configuration.
var sqlConnectionString = 'Server=tcp:${sqlServerFqdn},1433;Initial Catalog=${sqlDatabaseName};Authentication=Active Directory Default;User Id=${workloadIdentityClientId};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'

resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${baseName}-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalyticsCustomerId
        sharedKey: logAnalyticsSharedKey
      }
    }
    vnetConfiguration: {
      infrastructureSubnetId: appsSubnetId

      // Internal false: the web app is a public SaaS and must be reachable. Its dependencies
      // are not — they sit behind private endpoints on the same VNet.
      internal: false
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
  }
}

var sharedEnvironmentVariables = [
  {
    name: 'AZURE_CLIENT_ID'
    value: workloadIdentityClientId
  }
  {
    name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
    value: appInsightsConnectionString
  }
  {
    name: 'Mlcp__KeyVaultUri'
    value: keyVaultUri
  }
  {
    name: 'Mlcp__Region'
    value: tenantRegion
  }
  {
    name: 'Mlcp__ClientCertificateName'
    value: clientCertificateName
  }
  {
    name: 'AzureAd__ClientId'
    value: entraClientId
  }
  {
    name: 'ConnectionStrings__MlcpDatabase'
    value: sqlConnectionString
  }
]

resource webApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${baseName}-web'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${workloadIdentityId}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
      registries: [
        {
          server: containerRegistryServer
          identity: workloadIdentityId
        }
      ]
      secrets: [
        {
          name: 'redis-connection'

          // Resolved from Key Vault at runtime with the workload identity, so the access key
          // never enters the ARM deployment payload and rotating it needs no redeployment.
          keyVaultUrl: redisConnectionSecretUri
          identity: workloadIdentityId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'web'
          image: webImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: concat(sharedEnvironmentVariables, [
            {
              name: 'ConnectionStrings__Redis'
              secretRef: 'redis-connection'
            }
            {
              name: 'Mlcp__DataProtection__KeyUri'
              value: dataProtectionKeyUri
            }
            {
              name: 'Mlcp__DataProtection__BlobUri'
              value: '${dataProtectionBlobUri}/keys.xml'
            }
            {
              name: 'Mlcp__Email__Endpoint'
              value: emailEndpoint
            }
            {
              name: 'Mlcp__Email__SenderAddress'
              value: emailSenderAddress
            }
          ])
          probes: [
            {
              type: 'Readiness'
              httpGet: {
                path: '/health'
                port: 8080
              }
              periodSeconds: 10
            }
            {
              type: 'Liveness'
              httpGet: {
                path: '/health'
                port: 8080
              }
              periodSeconds: 30
            }
          ]
        }
      ]
      scale: {
        // At least two replicas: Data Protection keys and the MSAL cache are already shared, so
        // there is nothing to gain from a single instance and an outage to lose.
        minReplicas: 2
        maxReplicas: 10
        rules: [
          {
            name: 'http'
            http: {
              metadata: {
                concurrentRequests: '50'
              }
            }
          }
        ]
      }
    }
  }
}

resource syncWorker 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${baseName}-sync'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${workloadIdentityId}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'

      // No ingress: the worker is driven by a schedule and a queue, and exposing it would only
      // create an attack surface for something with cross-tenant database access.
      registries: [
        {
          server: containerRegistryServer
          identity: workloadIdentityId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'sync'
          image: syncImage
          resources: {
            cpu: json('1.0')
            memory: '2Gi'
          }
          env: concat(sharedEnvironmentVariables, [
            {
              name: 'Mlcp__ServiceBus__FullyQualifiedNamespace'
              value: serviceBusNamespaceFqdn
            }
          ])
        }
      ]
      scale: {
        // Exactly one scheduler. Duplicate detection on the queue makes a second replica
        // harmless rather than useful, and a single instance keeps the sweep behaviour easy to
        // reason about until queue depth justifies more.
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}

output webFqdn string = webApp.properties.configuration.ingress.fqdn
output webAppName string = webApp.name
output syncAppName string = syncWorker.name
output environmentId string = containerAppsEnvironment.id
