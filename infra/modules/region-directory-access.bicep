// Storage Table Data Contributor on the global tenant-region table (ADR-021) for one regional
// stack's web and worker identities.
//
// This module is scoped to the global resource group (infra/global.bicep). The grant covers the
// TenantRegions table only, not the whole account, so a regional stack can read or write nothing
// else in that storage account.

@description('Name of the global storage account in this resource group.')
param storageAccountName string

@description('Name of the tenant-region table.')
param tableName string = 'TenantRegions'

@description('Principal (object) ids that may read and write the table.')
param principalIds string[]

var storageTableDataContributorRoleId = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'

resource storage 'Microsoft.Storage/storageAccounts@2025-06-01' existing = {
  name: storageAccountName

  resource tableService 'tableServices' existing = {
    name: 'default'

    resource table 'tables' existing = {
      name: tableName
    }
  }
}

resource tableContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for principalId in principalIds: {
    scope: storage::tableService::table
    name: guid(storage::tableService::table.id, principalId, storageTableDataContributorRoleId)
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageTableDataContributorRoleId)
      principalId: principalId
      principalType: 'ServicePrincipal'
      description: 'MLCP regional stack: tenant-to-region directory'
    }
  }
]
