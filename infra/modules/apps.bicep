// Container Apps environment, the web application, the sync worker, and the manual jobs that run
// migrations and create the database users (ADR-026).
//
// Each workload runs as its own user-assigned identity:
// - web      → database user mlcp_web_user (role mlcp_web)
// - worker   → database user mlcp_worker_user (roles mlcp_worker, mlcp_system)
// - migrator → member of the SQL admin Entra group; used only by the jobs
//
// Customers never grant anything to these identities. Customer grants (Azure RBAC, billing roles)
// go to the MLCP enterprise application (service principal) in the customer's own tenant, and the
// apps act as that application using the Key Vault certificate.

@description('Azure region for all resources.')
param location string

@description('Resource name base, e.g. mlcp-prod-eu.')
param baseName string

@description('Region code recorded on tenants that connect through this stack (ADR-021).')
param regionCode string

@description('Subnet delegated to the Container Apps environment.')
param appsSubnetId string

@description('Web identity resource id and client id.')
param webIdentityId string
param webIdentityClientId string

@description('Worker identity resource id and client id.')
param workerIdentityId string
param workerIdentityClientId string

@description('Migrator identity resource id and client id.')
param migratorIdentityId string
param migratorIdentityClientId string

@description('Log Analytics workspace customer id for container logs.')
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

@description('Migration bundle image reference, including tag (src/Mlcp.Persistence/Dockerfile.migrate).')
param migrateImage string

@description('Suffix for the web revision this deployment creates. Must be unique per deployment.')
param webRevisionSuffix string

@description('Web revision that keeps 100% of traffic while the new one is verified. Empty on the first deployment, when the new revision takes all traffic.')
param webStableRevisionName string

@description('Entra application (client) id of the core MLCP registration.')
param entraClientId string

@description('Entra application (client) id of the MLCP Usage Insights registration (ADR-015).')
param usageInsightsClientId string

@description('Name of the client certificate in Key Vault. Both registrations use it.')
param clientCertificateName string

@description('Key Vault URI.')
param keyVaultUri string

@description('Versionless Data Protection key URI.')
param dataProtectionKeyUri string

@description('Data Protection blob container URI.')
param dataProtectionBlobUri string

@description('Service Bus fully qualified namespace.')
param serviceBusNamespaceFqdn string

@description('SQL server fully qualified domain name.')
param sqlServerFqdn string

@description('SQL database name.')
param sqlDatabaseName string

@description('Azure Managed Redis host name and port.')
param redisHostName string
param redisPort int

@description('Public base URL of this regional stack, e.g. https://eu.app.example.com.')
param publicBaseUrl string

@description('Host name of the global Front Door entry point, e.g. app.example.com.')
param globalHostName string

@description('Regional host names keyed by region code, e.g. { eu: \'eu.app.example.com\', us: \'us.app.example.com\' }.')
param regionHosts object

@description('Display names of the regions keyed by region code, shown on the Connect page (ADR-021).')
param regionNames object

@description('URI of the global TenantRegions table.')
param regionDirectoryTableUri string

@description('Raw URL of infra/customer-rbac.json, used by the Deploy to Azure button (Guide B).')
param customerRbacTemplateUrl string

@description('Azure Communication Services endpoint for outbound email. Empty disables sending.')
param emailEndpoint string

@description('Verified sender address for outbound email.')
param emailSenderAddress string

// Entra-only SQL, so there is no password anywhere. Each workload authenticates as its own
// managed identity, and the connection string is not a secret.
func sqlConnectionString(server string, database string, clientId string) string =>
  'Server=tcp:${server},1433;Initial Catalog=${database};Authentication=Active Directory Managed Identity;User Id=${clientId};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'

// Azure Managed Redis with Entra authentication: host and port only, no password. The app reads
// Mlcp:Redis:UseEntraAuth and authenticates with its managed identity
// (Microsoft.Azure.StackExchangeRedis).
var redisConnectionString = '${redisHostName}:${redisPort},ssl=True,abortConnect=False'

resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2026-01-01' = {
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

      // External, because the web app is a public SaaS. Its dependencies are not: they sit
      // behind private endpoints on the same VNet.
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

var allowedHosts = join(
  [
    split(publicBaseUrl, '/')[2]
    globalHostName

    // Revision-specific and default Container Apps host names: the deployment smoke test calls
    // the new revision directly, and Front Door forwards with the origin host name.
    '*.${containerAppsEnvironment.properties.defaultDomain}'
  ],
  ';'
)

var commonEnvironment = [
  {
    name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
    value: appInsightsConnectionString
  }
  {
    name: 'AzureAd__ClientId'
    value: entraClientId
  }
  {
    name: 'AzureAd__UsageInsightsClientId'
    value: usageInsightsClientId
  }
  {
    name: 'Mlcp__KeyVaultUri'
    value: keyVaultUri
  }
  {
    name: 'Mlcp__ClientCertificateName'
    value: clientCertificateName
  }
  {
    name: 'Mlcp__Region'
    value: regionCode
  }
  {
    name: 'Mlcp__Regions__DirectoryTableUri'
    value: regionDirectoryTableUri
  }
  {
    name: 'Mlcp__ServiceBus__FullyQualifiedNamespace'
    value: serviceBusNamespaceFqdn
  }
  {
    name: 'ConnectionStrings__Redis'
    value: redisConnectionString
  }
  {
    name: 'Mlcp__Redis__UseEntraAuth'
    value: 'true'
  }
]

var regionHostEnvironment = [
  for code in objectKeys(regionHosts): {
    name: 'Mlcp__Regions__Hosts__${code}'
    value: regionHosts[code]
  }
]

var regionNameEnvironment = [
  for code in objectKeys(regionNames): {
    name: 'Mlcp__Regions__Names__${code}'
    value: regionNames[code]
  }
]

var webEnvironment = concat(
  commonEnvironment,
  regionHostEnvironment,
  regionNameEnvironment,
  [
    {
      // Picked up by DefaultAzureCredential, the Data Protection providers and
      // Microsoft.Identity.Web, so each uses the web identity and not another one.
      name: 'AZURE_CLIENT_ID'
      value: webIdentityClientId
    }
    {
      name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED'
      value: 'true'
    }
    {
      name: 'AllowedHosts'
      value: allowedHosts
    }
    {
      name: 'ConnectionStrings__MlcpDatabase'
      value: sqlConnectionString(sqlServerFqdn, sqlDatabaseName, webIdentityClientId)
    }
    {
      // Sign-in code redemption needs a client credential. This is the Key Vault certificate,
      // loaded with the web identity; there is no client secret (ADR-026).
      name: 'AzureAd__ClientCredentials__0__SourceType'
      value: 'KeyVault'
    }
    {
      name: 'AzureAd__ClientCredentials__0__KeyVaultUrl'
      value: keyVaultUri
    }
    {
      name: 'AzureAd__ClientCredentials__0__KeyVaultCertificateName'
      value: clientCertificateName
    }
    {
      name: 'AzureAd__ClientCredentials__0__ManagedIdentityClientId'
      value: webIdentityClientId
    }
    {
      name: 'Mlcp__PublicBaseUrl'
      value: publicBaseUrl
    }
    {
      name: 'Mlcp__CustomerRbacTemplateUrl'
      value: customerRbacTemplateUrl
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
  ]
)

var workerEnvironment = concat(commonEnvironment, [
  {
    name: 'AZURE_CLIENT_ID'
    value: workerIdentityClientId
  }
  {
    name: 'ConnectionStrings__MlcpDatabase'
    value: sqlConnectionString(sqlServerFqdn, sqlDatabaseName, workerIdentityClientId)
  }
])

// Blue-green (https://learn.microsoft.com/en-us/azure/container-apps/blue-green-deployment).
// - First deployment: the new revision takes all traffic.
// - Later deployments: the stable revision keeps 100%. The new revision is named explicitly,
//   gets 0% and the label "candidate", so the workflow can migrate and smoke-test it at
//   https://<app>---candidate.<environment default domain> before moving traffic.
var webAppName = '${baseName}-web'
var webCandidateRevisionName = '${webAppName}--${webRevisionSuffix}'
var webTraffic = empty(webStableRevisionName) || webStableRevisionName == webCandidateRevisionName
  ? [
      {
        revisionName: webCandidateRevisionName
        weight: 100
      }
    ]
  : [
      {
        revisionName: webStableRevisionName
        weight: 100
      }
      {
        revisionName: webCandidateRevisionName
        weight: 0
        label: 'candidate'
      }
    ]

var httpPort = 8080

resource webApp 'Microsoft.App/containerApps@2026-01-01' = {
  name: webAppName
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${webIdentityId}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Multiple'
      ingress: {
        external: true
        targetPort: httpPort
        transport: 'auto'
        allowInsecure: false
        traffic: webTraffic
      }
      registries: [
        {
          server: containerRegistryServer
          identity: webIdentityId
        }
      ]
    }
    template: {
      revisionSuffix: webRevisionSuffix
      containers: [
        {
          name: 'web'
          image: webImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: webEnvironment
          probes: [
            {
              // Gives the app time to load the certificate and warm connections before the
              // other probes start counting.
              type: 'Startup'
              httpGet: {
                path: '/health/live'
                port: httpPort
              }
              initialDelaySeconds: 3
              periodSeconds: 5
              failureThreshold: 24
            }
            {
              // SQL, Redis and Key Vault reachability. A failing replica is taken out of
              // rotation rather than restarted.
              type: 'Readiness'
              httpGet: {
                path: '/health/ready'
                port: httpPort
              }
              periodSeconds: 10
              failureThreshold: 3
            }
            {
              // Process only. A dependency outage must not cause a restart loop.
              type: 'Liveness'
              httpGet: {
                path: '/health/live'
                port: httpPort
              }
              periodSeconds: 30
              failureThreshold: 3
            }
          ]
        }
      ]
      scale: {
        // At least two replicas. Data Protection keys and the MSAL cache are shared, so a single
        // instance gains nothing and risks an outage.
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

resource syncWorker 'Microsoft.App/containerApps@2026-01-01' = {
  name: '${baseName}-sync'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${workerIdentityId}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: {
      // Single revision. The deployment workflow passes the image that is already running, so a
      // deployment does not roll the worker ahead of the migrations. The workflow moves the
      // worker to the new image after the migrate job succeeds.
      activeRevisionsMode: 'Single'

      // No ingress. The worker runs on a schedule and a queue. Exposing it would only add attack
      // surface to a process with cross-tenant database access.
      registries: [
        {
          server: containerRegistryServer
          identity: workerIdentityId
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
          env: workerEnvironment
        }
      ]
      scale: {
        // Exactly one scheduler per region (ADR-026). Session-enabled queues and per-run app locks
        // (ADR-024) make a second replica harmless, not useful.
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}

// ------------------------------------------------------------------------------------------
// Manual jobs, run by the deployment workflow inside the VNet as the migrator identity.
// ------------------------------------------------------------------------------------------
var migratorSqlConnection = sqlConnectionString(sqlServerFqdn, sqlDatabaseName, migratorIdentityClientId)

var jobConfiguration = {
  triggerType: 'Manual'
  replicaTimeout: 1800
  replicaRetryLimit: 0
  manualTriggerConfig: {
    parallelism: 1
    replicaCompletionCount: 1
  }
  registries: [
    {
      server: containerRegistryServer
      identity: migratorIdentityId
    }
  ]
}

resource migrateJob 'Microsoft.App/jobs@2026-01-01' = {
  name: '${baseName}-migrate'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${migratorIdentityId}': {}
    }
  }
  properties: {
    environmentId: containerAppsEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: jobConfiguration
    template: {
      containers: [
        {
          name: 'migrate'
          image: migrateImage
          command: ['/app/efbundle']
          args: [
            '--connection'
            migratorSqlConnection
            '--verbose'
          ]
          env: [
            {
              name: 'AZURE_CLIENT_ID'
              value: migratorIdentityClientId
            }
          ]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
    }
  }
}

resource createUsersJob 'Microsoft.App/jobs@2026-01-01' = {
  name: '${baseName}-create-users'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${migratorIdentityId}': {}
    }
  }
  properties: {
    environmentId: containerAppsEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: jobConfiguration
    template: {
      containers: [
        {
          name: 'create-users'
          image: migrateImage
          command: ['/opt/sqlcmd/sqlcmd']
          args: [
            '-S'
            'tcp:${sqlServerFqdn},1433'
            '-d'
            sqlDatabaseName
            '--authentication-method'
            'ActiveDirectoryManagedIdentity'
            '-U'
            migratorIdentityClientId
            '-b'
            '-i'
            '/app/sql/create-users.sql'
            // One -v per variable: portable across go-sqlcmd and ODBC sqlcmd.
            '-v'
            'WebUserName=mlcp_web_user'
            '-v'
            'WebClientId=${webIdentityClientId}'
            '-v'
            'WorkerUserName=mlcp_worker_user'
            '-v'
            'WorkerClientId=${workerIdentityClientId}'
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
    }
  }
}

output webFqdn string = webApp.properties.configuration.ingress.fqdn
output webAppName string = webApp.name
output syncAppName string = syncWorker.name
output migrateJobName string = migrateJob.name
output createUsersJobName string = createUsersJob.name
output environmentDefaultDomain string = containerAppsEnvironment.properties.defaultDomain
output environmentId string = containerAppsEnvironment.id
