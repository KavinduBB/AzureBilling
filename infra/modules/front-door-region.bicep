// One regional Front Door endpoint (<code>.app.<domain>) with its own origin group (ADR-021).

@description('Name of the existing Front Door profile.')
param profileName string

@description('Endpoint name.')
param endpointName string

@description('Region code, e.g. eu.')
param regionCode string

@description('Host name of the regional web app.')
param originHostName string

@description('Health probe settings shared with the global origin group.')
param healthProbeSettings object

@description('Load-balancing settings shared with the global origin group.')
param loadBalancingSettings object

@description('Route properties shared with the global route, without the origin group.')
param routeProperties object

resource profile 'Microsoft.Cdn/profiles@2025-12-01' existing = {
  name: profileName
}

resource originGroup 'Microsoft.Cdn/profiles/originGroups@2025-12-01' = {
  parent: profile
  name: 'region-${regionCode}'
  properties: {
    loadBalancingSettings: loadBalancingSettings
    healthProbeSettings: healthProbeSettings
    sessionAffinityState: 'Disabled'
  }
}

resource origin 'Microsoft.Cdn/profiles/originGroups/origins@2025-12-01' = {
  parent: originGroup
  name: regionCode
  properties: {
    hostName: originHostName
    originHostHeader: originHostName
    httpPort: 80
    httpsPort: 443
    priority: 1
    weight: 1000
    enabledState: 'Enabled'
    enforceCertificateNameCheck: true
  }
}

resource endpoint 'Microsoft.Cdn/profiles/afdEndpoints@2025-12-01' = {
  parent: profile
  name: endpointName
  location: 'global'
  properties: {
    enabledState: 'Enabled'
  }
}

resource route 'Microsoft.Cdn/profiles/afdEndpoints/routes@2025-12-01' = {
  parent: endpoint
  name: 'default'
  properties: union(routeProperties, {
    originGroup: {
      id: originGroup.id
    }
  })
  dependsOn: [
    origin
  ]
}

output hostName string = endpoint.properties.hostName
