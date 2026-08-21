// Subscription-scoped so the resource group itself is part of the deployment. Everything the
// system needs is declared here -- API Management, the Foundry account and project, the model
// deployments, and the function app -- so a subscription that contains nothing can be brought up in
// one pass. The only piece outside this template is the Entra app registration whose App ID URI the
// gateway validates, because Entra objects are not ARM resources;
// scripts/New-GeoApiAppRegistration.ps1 creates it in the preprovision hook.
targetScope = 'subscription'

@minLength(1)
@maxLength(20)
@description('Name of the azd environment. Tags every resource and seeds every resource name.')
param environmentName string

@minLength(1)
@description('Region for the resource group and everything in it.')
param location string

@description('Resource group to create. Defaults to rg-<environment name>.')
param resourceGroupName string = 'rg-${environmentName}'

@description('Object id of the principal running the deployment. Foundry data-plane access is not implied by subscription Owner, so without this the agents cannot be published afterwards.')
param deployerPrincipalId string = ''

@allowed([
  'User'
  'ServicePrincipal'
])
param deployerPrincipalType string = 'User'

@minLength(1)
@description('Contact string the National Weather Service requires on every request. There is no default; the endpoint returns 503 without it.')
param nwsUserAgent string

@description('Developer has no SLA and takes 30-45 minutes to provision, but is the cheapest tier that still supports the rate-limit-by-key policy the geo API applies.')
@allowed([
  'Developer'
  'Basicv2'
  'Standardv2'
  'Premium'
])
param apimSku string = 'Developer'

param apimPublisherName string = 'Geo-Location Agents'

@minLength(1)
@description('Address API Management sends service notifications to.')
param apimPublisherEmail string

param apimApiId string = 'geo-api'
param apimApiPath string = 'geo'

// The Foundry Agent Service can host the orchestrator itself, which leaves one less resource to own
// and one less identity to grant. It is a commercial-only offering today, so anywhere else the same
// binary runs on App Service behind the same gateway. If it reaches Government, this map is the
// only line that has to change.
@description('Where the orchestrator runs. Empty resolves it from the cloud being deployed to.')
@allowed([
  ''
  'FoundryHosted'
  'LinuxAppService'
])
param orchestratorHost string = ''

@description('App ID URI of the app registration representing the orchestrator API. Required only when the orchestrator is self-hosted; the preprovision hook writes it into the environment.')
param orchestratorApiAudience string = ''

param orchestratorApiId string = 'orchestrator-api'
param orchestratorApiPath string = 'orchestrator'

@description('App Service tier for the orchestrator. Empty resolves it from the cloud being deployed to.')
param orchestratorAppServiceSku string = ''

// B1 and S1 sit on older worker pools in Government that are chronically full, and the deployment
// fails with 'No available instances to satisfy this request'. That is a scale unit out of room
// rather than the SKU being absent, so it does not show up in a regional SKU list and there is
// nothing to check before deploying. Premium v3 runs on newer stamps that have capacity.
var appServiceSkuByCloud = {
  AzureCloud: 'B1'
  AzureUSGovernment: 'P0v3'
}
var resolvedOrchestratorAppServiceSku = !empty(orchestratorAppServiceSku)
  ? orchestratorAppServiceSku
  : (appServiceSkuByCloud[?environment().name] ?? 'B1')

var orchestratorHostByCloud = {
  AzureCloud: 'FoundryHosted'
  AzureUSGovernment: 'LinuxAppService'
}
var resolvedOrchestratorHost = !empty(orchestratorHost)
  ? orchestratorHost
  : (orchestratorHostByCloud[?environment().name] ?? 'FoundryHosted')
var selfHostedOrchestrator = resolvedOrchestratorHost == 'LinuxAppService'

param entraTenantId string = subscription().tenantId

// Every other hostname in this template is read back from the resource that owns it, or comes from
// environment(). These three have no such source, so they are the only ones a sovereign cloud has
// to be told about.
@description('Issuer prefix for v1 tokens, which is what a managed identity presents.')
param entraV1Issuer string = 'https://sts.windows.net/'

@description('Azure Maps data plane. Empty resolves it from the cloud being deployed to.')
param azureMapsEndpoint string = ''

@description('DNS domain of the Foundry data plane. Empty resolves it from the cloud being deployed to.')
param aiServicesDomain string = ''

@description('SKU for both model deployments. Empty resolves it from the cloud being deployed to.')
param modelDeploymentSku string = ''

// Foundry keeps agent records, and the Entra agent identities they point at, keyed to the account's
// subdomain. Deleting the account does not clear them, so a rebuilt environment that lands on the
// same name inherits agent versions whose identities the teardown already deleted, and every
// invocation fails with 'Agent Identity Blueprint has been deleted or disabled'. The salt is
// generated once per environment and keeps each rebuild on a subdomain of its own.
@description('Per-environment entropy for resource names. Set once when the environment is created; changing it renames every resource.')
param resourceTokenSalt string = ''

var azureMapsEndpointByCloud = {
  AzureCloud: 'https://atlas.microsoft.com'
  AzureUSGovernment: 'https://atlas.azure.us'
}
var resolvedAzureMapsEndpoint = !empty(azureMapsEndpoint)
  ? azureMapsEndpoint
  : (azureMapsEndpointByCloud[?environment().name] ?? 'https://atlas.microsoft.com')

// Flex Consumption scales to zero and is the cheaper way to run this, but it is not offered in
// Government, and the tiers that are there all require a content share that only account keys can
// reach. Dedicated is the one tier with no content share, so it is the only one that survives a
// tenant policy disabling shared key access.
@description('Hosting tier for the function app. Empty resolves it from the cloud being deployed to.')
@allowed([
  ''
  'FlexConsumption'
  'Dedicated'
])
param functionPlanTier string = ''

@description('App Service tier used when the function app lands on a Dedicated plan. Empty resolves it from the cloud being deployed to.')
param functionAppServiceSku string = ''

var resolvedFunctionAppServiceSku = !empty(functionAppServiceSku)
  ? functionAppServiceSku
  : (appServiceSkuByCloud[?environment().name] ?? 'B1')

var functionPlanTierByCloud = {
  AzureCloud: 'FlexConsumption'
  AzureUSGovernment: 'Dedicated'
}
var resolvedFunctionPlanTier = !empty(functionPlanTier)
  ? functionPlanTier
  : (functionPlanTierByCloud[?environment().name] ?? 'FlexConsumption')

@minLength(1)
@description('App ID URI of the app registration representing this API. The preprovision hook writes it into the environment.')
param geoApiAudience string

param orchestratorDeploymentName string = 'gpt-4.1'
param orchestratorModelName string = 'gpt-4.1'
param orchestratorModelVersion string = '2025-04-14'
param orchestratorCapacity int = 50

param specialistDeploymentName string = 'gpt-4-1-mini'
param specialistModelName string = 'gpt-4.1-mini'
param specialistModelVersion string = '2025-04-14'
param specialistCapacity int = 50

var tags = {
  'azd-env-name': environmentName
}

// Names are derived rather than supplied, so a fresh environment needs only the inputs above.
var resourceToken = toLower(uniqueString(subscription().id, environmentName, location, resourceTokenSalt))
var namePrefix = toLower(replace(environmentName, '_', '-'))
var alphaPrefix = take(replace(namePrefix, '-', ''), 9)

var functionAppName = 'func-${namePrefix}-${resourceToken}'
var storageAccountName = 'st${alphaPrefix}${resourceToken}'
var hostingPlanName = 'plan-${namePrefix}-${resourceToken}'
var appInsightsName = 'appi-${namePrefix}-${resourceToken}'
var logAnalyticsWorkspaceName = 'log-${namePrefix}-${resourceToken}'
var mapsAccountName = 'maps-${namePrefix}-${resourceToken}'
var apimServiceName = 'apim-${namePrefix}-${resourceToken}'
var foundryAccountName = 'aif-${namePrefix}-${resourceToken}'
var foundryProjectName = 'proj-${namePrefix}'
var orchestratorAppName = 'app-${namePrefix}-${resourceToken}'
var orchestratorPlanName = 'plan-orch-${namePrefix}-${resourceToken}'
var orchestratorIdentityName = 'id-orch-${namePrefix}-${resourceToken}'
var functionIdentityName = 'id-func-${namePrefix}-${resourceToken}'
var deploymentContainerName = 'app-package-${take(replace(functionAppName, '-', ''), 32)}-${take(resourceToken, 7)}'

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

module backend 'modules/backend.bicep' = {
  name: 'backend'
  scope: rg
  params: {
    location: location
    tags: tags
    functionAppName: functionAppName
    storageAccountName: storageAccountName
    hostingPlanName: hostingPlanName
    appInsightsName: appInsightsName
    logAnalyticsWorkspaceName: logAnalyticsWorkspaceName
    deploymentContainerName: deploymentContainerName
    nwsUserAgent: nwsUserAgent
    mapsAccountName: mapsAccountName
    azureMapsEndpoint: resolvedAzureMapsEndpoint
    functionPlanTier: resolvedFunctionPlanTier
    functionAppServiceSku: resolvedFunctionAppServiceSku
    functionIdentityName: functionIdentityName
  }
}

module apim 'modules/apim.bicep' = {
  name: 'apim'
  scope: rg
  params: {
    location: location
    tags: tags
    apimServiceName: apimServiceName
    publisherName: apimPublisherName
    publisherEmail: apimPublisherEmail
    sku: apimSku
  }
}

// Deployed even where nothing uses it, so that the Foundry role assignment below can name a
// principal that already exists rather than one App Service would have to be created first to mint.
module orchestratorIdentity 'modules/orchestrator-identity.bicep' = {
  name: 'orchestrator-identity'
  scope: rg
  params: {
    location: location
    tags: tags
    name: orchestratorIdentityName
  }
}

module foundry 'modules/foundry.bicep' = {
  name: 'foundry'
  scope: rg
  params: {
    location: location
    tags: tags
    accountName: foundryAccountName
    projectName: foundryProjectName
    orchestratorDeploymentName: orchestratorDeploymentName
    orchestratorModelName: orchestratorModelName
    orchestratorModelVersion: orchestratorModelVersion
    orchestratorCapacity: orchestratorCapacity
    specialistDeploymentName: specialistDeploymentName
    specialistModelName: specialistModelName
    specialistModelVersion: specialistModelVersion
    specialistCapacity: specialistCapacity
    deployerPrincipalId: deployerPrincipalId
    deployerPrincipalType: deployerPrincipalType
    orchestratorPrincipalId: selfHostedOrchestrator ? orchestratorIdentity.outputs.principalId : ''
    aiServicesDomain: aiServicesDomain
    modelDeploymentSku: modelDeploymentSku
  }
}

// Last, because its policy pins the Foundry identity and its named values carry the function host key.
module geoApi 'modules/apim-geo-api.bicep' = {
  name: 'geo-api'
  scope: rg
  params: {
    apimServiceName: apim.outputs.apimServiceName
    apiId: apimApiId
    apiPath: apimApiPath
    functionAppResourceGroup: rg.name
    functionAppName: functionAppName
    functionAppApiUrl: backend.outputs.functionAppApiUrl
    entraTenantId: entraTenantId
    entraV1Issuer: entraV1Issuer
    geoApiAudience: geoApiAudience
    foundryMiPrincipalId: foundry.outputs.accountPrincipalId
  }
}

module orchestratorApp 'modules/orchestrator-appservice.bicep' = if (selfHostedOrchestrator) {
  name: 'orchestrator-app'
  scope: rg
  params: {
    location: location
    tags: tags
    name: orchestratorAppName
    planName: orchestratorPlanName
    sku: resolvedOrchestratorAppServiceSku
    identityResourceId: orchestratorIdentity.outputs.id
    identityClientId: orchestratorIdentity.outputs.clientId
    foundryProjectEndpoint: foundry.outputs.projectEndpoint
    modelDeploymentName: foundry.outputs.orchestratorDeploymentName
    foundryTokenAudience: foundry.outputs.tokenAudience
    appInsightsConnectionString: backend.outputs.appInsightsConnectionString
    callerPrincipalId: apim.outputs.principalId
    authClientId: replace(orchestratorApiAudience, 'api://', '')
    authAudience: orchestratorApiAudience
    entraV1Issuer: entraV1Issuer
  }
}

module orchestratorApi 'modules/apim-orchestrator-api.bicep' = if (selfHostedOrchestrator) {
  name: 'orchestrator-api'
  scope: rg
  params: {
    apimServiceName: apim.outputs.apimServiceName
    apiId: orchestratorApiId
    apiPath: orchestratorApiPath
    orchestratorUrl: orchestratorApp!.outputs.url
    orchestratorApiAudience: orchestratorApiAudience
  }
  // Its policy reads the tenant, authority, and issuer named values the geo API declares.
  dependsOn: [
    geoApi
  ]
}

output AZURE_RESOURCE_GROUP string = rg.name
output FUNCTION_APP_RESOURCE_GROUP string = rg.name
output FUNCTION_APP_NAME string = backend.outputs.functionAppName
output FUNCTION_APP_API_URL string = backend.outputs.functionAppApiUrl
output FUNCTION_APP_PRINCIPAL_ID string = backend.outputs.functionAppPrincipalId
output FUNCTION_PLAN_TIER string = resolvedFunctionPlanTier
output MAPS_ACCOUNT_NAME string = backend.outputs.mapsAccountName

output APIM_RESOURCE_GROUP string = rg.name
output APIM_SERVICE_NAME string = apim.outputs.apimServiceName
output APIM_API_ID string = apimApiId
output APIM_API_PATH string = apimApiPath
output GEO_API_BASE_URL string = geoApi.outputs.gatewayApiUrl

output FOUNDRY_RESOURCE_GROUP string = rg.name
output FOUNDRY_ACCOUNT_NAME string = foundry.outputs.accountName
output FOUNDRY_PROJECT_NAME string = foundry.outputs.projectName
output FOUNDRY_PROJECT_ENDPOINT string = foundry.outputs.projectEndpoint
output FOUNDRY_TOKEN_AUDIENCE string = foundry.outputs.tokenAudience
output FOUNDRY_MI_PRINCIPAL_ID string = foundry.outputs.accountPrincipalId
output ORCHESTRATOR_MODEL string = foundry.outputs.orchestratorDeploymentName
output SPECIALIST_MODEL string = foundry.outputs.specialistDeploymentName
output AZURE_AI_MODEL_DEPLOYMENT_NAME string = foundry.outputs.orchestratorDeploymentName

// Empty under FoundryHosted, which is what New-Deployment.ps1 and ask.ps1 branch on.
output ORCHESTRATOR_HOST string = resolvedOrchestratorHost
output ORCHESTRATOR_APP_NAME string = selfHostedOrchestrator ? orchestratorApp!.outputs.name : ''
output ORCHESTRATOR_URL string = selfHostedOrchestrator ? orchestratorApp!.outputs.url : ''
output ORCHESTRATOR_API_BASE_URL string = selfHostedOrchestrator ? orchestratorApi!.outputs.gatewayApiUrl : ''
