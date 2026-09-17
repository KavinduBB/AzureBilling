// AcrPull on the shared container registry for the web, worker and migrator identities (ADR-026).
//
// This module is scoped to the registry's resource group, which regions and environments usually
// share. Without this grant, the first deployment cannot pull its images.

@description('Name of the existing container registry in this resource group.')
param containerRegistryName string

@description('Principal (object) ids that may pull images.')
param principalIds string[]

var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource registry 'Microsoft.ContainerRegistry/registries@2025-11-01' existing = {
  name: containerRegistryName
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for principalId in principalIds: {
    scope: registry
    name: guid(registry.id, principalId, acrPullRoleId)
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
      principalId: principalId
      principalType: 'ServicePrincipal'
      description: 'MLCP: pull application images'
    }
  }
]

output loginServer string = registry.properties.loginServer
