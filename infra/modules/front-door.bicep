// Azure Front Door Standard/Premium for MLCP (ADR-021). It provides:
// - a global endpoint (app.<domain>) that load-balances across all regions;
// - one endpoint per region (eu.app.<domain>, us.app.<domain>), each with its own origin group.

@description('Resource name base, e.g. mlcp-prod-global.')
param baseName string

@description('Prefix for regional endpoint names, e.g. mlcp-prod.')
param endpointPrefix string

@description('Regional web origins: region code and the host name Front Door forwards to.')
param regionalOrigins {
  code: string
  hostName: string
}[]

@description('Region preferred by the global endpoint when latency is equal.')
param defaultRegionCode string

@description('Front Door SKU.')
param frontDoorSku string

resource profile 'Microsoft.Cdn/profiles@2025-12-01' = {
  name: '${baseName}-afd'
  location: 'global'
  sku: {
    name: frontDoorSku
  }
  properties: {
    originResponseTimeoutSeconds: 60
  }
}

var healthProbeSettings = {
  probePath: '/health/ready'
  probeRequestType: 'GET'
  probeProtocol: 'Https'
  probeIntervalInSeconds: 60
}

var loadBalancingSettings = {
  sampleSize: 4
  successfulSamplesRequired: 3
  additionalLatencyInMilliseconds: 50
}

// The global entry point. Session affinity keeps a browser on one region for the whole OIDC round
// trip, because the correlation cookie is protected with that region's Data Protection keys. After
// sign-in, RegionRoutingMiddleware redirects the user to their tenant's regional host.
resource globalOriginGroup 'Microsoft.Cdn/profiles/originGroups@2025-12-01' = {
  parent: profile
  name: 'all-regions'
  properties: {
    loadBalancingSettings: loadBalancingSettings
    healthProbeSettings: healthProbeSettings
    sessionAffinityState: 'Enabled'
  }
}

resource globalOrigins 'Microsoft.Cdn/profiles/originGroups/origins@2025-12-01' = [
  for origin in regionalOrigins: {
    parent: globalOriginGroup
    name: origin.code
    properties: {
      // Container Apps routes by host name, so Front Door sends the origin's own host name.
      hostName: origin.hostName
      originHostHeader: origin.hostName
      httpPort: 80
      httpsPort: 443
      // Lower value wins: the default region is preferred while it is healthy.
      priority: origin.code == defaultRegionCode ? 1 : 2
      weight: 1000
      enabledState: 'Enabled'
      enforceCertificateNameCheck: true
    }
  }
]

resource globalEndpoint 'Microsoft.Cdn/profiles/afdEndpoints@2025-12-01' = {
  parent: profile
  name: '${baseName}-app'
  location: 'global'
  properties: {
    enabledState: 'Enabled'
  }
}

var routeProperties = {
  supportedProtocols: [
    'Http'
    'Https'
  ]
  patternsToMatch: [
    '/*'
  ]
  forwardingProtocol: 'HttpsOnly'
  linkToDefaultDomain: 'Enabled'
  httpsRedirect: 'Enabled'
  enabledState: 'Enabled'
}

resource globalRoute 'Microsoft.Cdn/profiles/afdEndpoints/routes@2025-12-01' = {
  parent: globalEndpoint
  name: 'default'
  properties: union(routeProperties, {
    originGroup: {
      id: globalOriginGroup.id
    }
  })
  dependsOn: [
    globalOrigins
  ]
}

// One origin group, endpoint and route per region.
module region 'front-door-region.bicep' = [
  for origin in regionalOrigins: {
    name: 'afd-region-${origin.code}'
    params: {
      profileName: profile.name
      endpointName: '${endpointPrefix}-${origin.code}-app'
      regionCode: origin.code
      originHostName: origin.hostName
      healthProbeSettings: healthProbeSettings
      loadBalancingSettings: loadBalancingSettings
      routeProperties: routeProperties
    }
  }
]

output globalHostName string = globalEndpoint.properties.hostName

output regionalHostNames array = [
  for (origin, i) in regionalOrigins: {
    code: origin.code
    hostName: region[i].outputs.hostName
  }
]

output profileName string = profile.name
