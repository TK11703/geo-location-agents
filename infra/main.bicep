// Subscription-scoped because the function app and APIM live in different resource groups.
// APIM, the Foundry account, and the model deployments are shared with other workloads and are
// referenced, never declared.
targetScope = 'subscription'

@minLength(1)
@description('Name of the azd environment, used to tag resources.')
param environmentName string

@minLength(1)
param location string

param functionAppResourceGroup string
param functionAppName string
param storageAccountName string
param hostingPlanName string
param appInsightsName string
param logAnalyticsWorkspaceId string
param deploymentContainerName string

param nwsUserAgent string

@secure()
@description('Azure Maps key. Sourced from the azd environment; never committed.')
param azureMapsSubscriptionKey string

param apimResourceGroup string
param apimServiceName string
param apimApiId string
param apimApiPath string

param entraTenantId string
param geoApiAudience string
param foundryMiPrincipalId string

var tags = {
  'azd-env-name': environmentName
}

resource backendRg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: functionAppResourceGroup
  location: location
  tags: tags
}

module backend 'modules/backend.bicep' = {
  name: 'backend'
  scope: backendRg
  params: {
    location: location
    tags: tags
    functionAppName: functionAppName
    storageAccountName: storageAccountName
    hostingPlanName: hostingPlanName
    appInsightsName: appInsightsName
    logAnalyticsWorkspaceId: logAnalyticsWorkspaceId
    deploymentContainerName: deploymentContainerName
    nwsUserAgent: nwsUserAgent
    azureMapsSubscriptionKey: azureMapsSubscriptionKey
  }
}

resource apimRg 'Microsoft.Resources/resourceGroups@2024-03-01' existing = {
  name: apimResourceGroup
}

module geoApi 'modules/apim-geo-api.bicep' = {
  name: 'geo-api'
  scope: apimRg
  params: {
    apimServiceName: apimServiceName
    apiId: apimApiId
    apiPath: apimApiPath
    functionAppResourceGroup: functionAppResourceGroup
    functionAppName: functionAppName
    functionAppApiUrl: backend.outputs.functionAppApiUrl
    entraTenantId: entraTenantId
    geoApiAudience: geoApiAudience
    foundryMiPrincipalId: foundryMiPrincipalId
  }
}

output FUNCTION_APP_NAME string = backend.outputs.functionAppName
output FUNCTION_APP_API_URL string = backend.outputs.functionAppApiUrl
output FUNCTION_APP_PRINCIPAL_ID string = backend.outputs.functionAppPrincipalId
output GEO_API_BASE_URL string = geoApi.outputs.gatewayApiUrl
