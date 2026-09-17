// One-click Azure role assignment for MLCP, deployed by the CUSTOMER in THEIR tenant
// (docs/07-onboarding-guides.md, Guide B).
//
// Deployment scope: a management group. The Tenant Root Group covers every current and future
// subscription for EA and pay-as-you-go (MOSP) subscriptions. Microsoft Cost Management does not
// support management groups for Microsoft Customer Agreement subscriptions or CSP scopes, so MCA
// and CSP customers should also grant a billing role (Guide C); until then MLCP queries each
// subscription separately (ADR-017).
//
//   az deployment mg create \
//     --management-group-id <tenant root group id, which equals your tenant id> \
//     --location <any Azure region> \
//     --template-file customer-rbac.bicep \
//     --parameters mlcpPrincipalId=<object id of the MLCP enterprise application in your tenant>
//
// The portal "Deploy to Azure" button uses infra/customer-rbac.json, which is generated from this
// file with:
//   az bicep build --file infra/customer-rbac.bicep --outfile infra/customer-rbac.json
// CI fails if the JSON is out of date.

targetScope = 'managementGroup'

@description('Object ID of the MLCP enterprise application (service principal) in YOUR tenant. Find it in the Microsoft Entra admin center under Enterprise applications > MLCP > Overview > Object ID. It exists once your administrator has connected MLCP. Do not use the Application (client) ID.')
@minLength(36)
@maxLength(36)
param mlcpPrincipalId string

// Built-in role: Cost Management Reader. Read-only access to cost data and cost configuration.
var costManagementReaderRoleId = '72fafb9e-0641-4937-9268-a91bfd8191a3'

resource costReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(managementGroup().id, mlcpPrincipalId, costManagementReaderRoleId)
  properties: {
    // Built-in role definitions are tenant-level resources. At management-group scope they are
    // referenced with tenantResourceId, not subscriptionResourceId.
    roleDefinitionId: tenantResourceId('Microsoft.Authorization/roleDefinitions', costManagementReaderRoleId)
    principalId: mlcpPrincipalId
    principalType: 'ServicePrincipal'
    description: 'MLCP read-only cost visibility'
  }
}

@description('Scope the role was assigned at.')
output assignedScope string = managementGroup().id
