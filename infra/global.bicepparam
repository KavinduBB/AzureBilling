// Parameters for the global resources (infra/global.bicep).
//
// First deployment: leave MLCP_ORIGIN_EU / MLCP_ORIGIN_US unset. Only the tenant directory is
// created, because the regional stacks need its outputs.
// After the regional stacks exist: set each variable to that region's main.bicep output webFqdn and
// deploy again to create Front Door.
using 'global.bicep'

param environmentName = readEnvironmentVariable('MLCP_ENVIRONMENT', 'prod')
param location = 'westeurope'
param defaultRegionCode = 'eu'

var euOrigin = readEnvironmentVariable('MLCP_ORIGIN_EU', '')
var usOrigin = readEnvironmentVariable('MLCP_ORIGIN_US', '')

param regionalOrigins = filter(
  [
    {
      code: 'eu'
      hostName: euOrigin
    }
    {
      code: 'us'
      hostName: usOrigin
    }
  ],
  origin => !empty(origin.hostName)
)
