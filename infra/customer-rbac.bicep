// One-click role assignment for MLCP's service principal.
// Deploy at management-group scope (Tenant Root Group recommended).
// az deployment mg create --management-group-id <rootMgId> --location <region> --template-file customer-rbac.bicep --parameters mlcpPrincipalId=<objectId>
targetScope = 'managementGroup'

@description('Object ID of the MLCP enterprise application (service principal) in YOUR tenant, created when you consented to the app.')
param mlcpPrincipalId string

// Built-in role: Cost Management Reader
var costManagementReaderRoleId = '72fafb9e-0641-4937-9268-a91bfd8191a3'

resource costReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(managementGroup().id, mlcpPrincipalId, costManagementReaderRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', costManagementReaderRoleId)
    principalId: mlcpPrincipalId
    principalType: 'ServicePrincipal'
    description: 'MLCP read-only cost visibility'
  }
}
