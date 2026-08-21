// Microsoft Foundry account, project, and the two model deployments the agents run on.
// The account's system-assigned identity is what the specialists present to the gateway, so its
// principal id is an output rather than a value copied between environments by hand.

param accountName string
param projectName string
param location string

param projectDisplayName string = 'Geo-Location Agents'
param projectDescription string = 'Geospatial specialist and orchestrator agents.'

@description('Deployment the orchestrator runs on.')
param orchestratorDeploymentName string
param orchestratorModelName string
param orchestratorModelVersion string
param orchestratorCapacity int

@description('Deployment the four specialists run on. Smaller, because each one answers a single narrow question.')
param specialistDeploymentName string
param specialistModelName string
param specialistModelVersion string
param specialistCapacity int

@description('Principal running the deployment. Foundry data-plane access is not implied by subscription Owner, so publishing agents fails without this.')
param deployerPrincipalId string = ''

@description('Identity a self-hosted orchestrator runs as. Empty when the Agent Service hosts it, because then the data-plane access is its own.')
param orchestratorPrincipalId string = ''

@allowed([
  'User'
  'ServicePrincipal'
])
param deployerPrincipalType string = 'User'

param tags object = {}

// environment() carries no suffix for the Foundry data plane, so the cloud's domain is resolved
// here and both hostnames below are built from it.
@description('DNS domain of the Foundry data plane. Empty resolves it from the cloud being deployed to.')
param aiServicesDomain string = ''

@description('SKU for both model deployments. Empty resolves it from the cloud being deployed to.')
param modelDeploymentSku string = ''

var aiServicesDomainByCloud = {
  AzureCloud: 'azure.com'
  AzureUSGovernment: 'azure.us'
}
var resolvedAiServicesDomain = !empty(aiServicesDomain)
  ? aiServicesDomain
  : (aiServicesDomainByCloud[?environment().name] ?? 'azure.com')

// GlobalStandard routes across the commercial fleet and is not offered in Azure Government, which
// has Standard and DataZoneStandard instead.
var modelSkuByCloud = {
  AzureCloud: 'GlobalStandard'
  AzureUSGovernment: 'Standard'
}
var resolvedModelSku = !empty(modelDeploymentSku)
  ? modelDeploymentSku
  : (modelSkuByCloud[?environment().name] ?? 'GlobalStandard')

// Azure AI User and Azure AI Project Manager are not published in Government, so an assignment
// naming either fails with RoleDefinitionDoesNotExist. Cognitive Services User is, and its
// Microsoft.CognitiveServices/* data action covers both the agent CRUD deploy-agents.ps1 performs
// and the inference the orchestrator does. It is broader than the pair it replaces, but both
// assignments below are scoped to this account alone.
var foundryUserRoleByCloud = {
  AzureCloud: '53ca6127-db72-4b80-b1b0-d745d6d5456d'
  AzureUSGovernment: 'a97b65f3-24c7-4388-baec-2e87135dc908'
}

// Empty means the wildcard above already covers it, and the separate assignment is skipped.
var projectManagerRoleByCloud = {
  AzureCloud: 'eadc314b-1a2d-4efa-be10-5d325db5065e'
  AzureUSGovernment: ''
}

var resolvedFoundryUserRole = foundryUserRoleByCloud[?environment().name] ?? foundryUserRoleByCloud.AzureCloud
var resolvedProjectManagerRole = projectManagerRoleByCloud[?environment().name] ?? projectManagerRoleByCloud.AzureCloud

var foundryUser = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', resolvedFoundryUserRole)
var foundryProjectManager = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', resolvedProjectManagerRole)

resource account 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: accountName
  location: location
  tags: tags
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    // Without this the account cannot hold projects, and agents live on a project.
    allowProjectManagement: true
    // Fixes the account's data-plane hostname, which the project endpoint below is built from.
    customSubDomainName: accountName
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: false
  }
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  parent: account
  name: projectName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    displayName: projectDisplayName
    description: projectDescription
  }
}

resource orchestratorModel 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  parent: account
  name: orchestratorDeploymentName
  sku: {
    name: resolvedModelSku
    capacity: orchestratorCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: orchestratorModelName
      version: orchestratorModelVersion
    }
  }
  // The account serializes operations across all its children, not just its deployments, so a
  // project created alongside one of them loses the race with RequestConflict.
  dependsOn: [
    project
  ]
}

resource specialistModel 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  parent: account
  name: specialistDeploymentName
  sku: {
    name: resolvedModelSku
    capacity: specialistCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: specialistModelName
      version: specialistModelVersion
    }
  }
  // Deployments on one account are applied serially; in parallel the second gets a conflict.
  dependsOn: [
    orchestratorModel
  ]
}

resource deployerFoundryUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerPrincipalId)) {
  scope: account
  name: guid(account.id, deployerPrincipalId, foundryUser)
  properties: {
    principalId: deployerPrincipalId
    roleDefinitionId: foundryUser
    principalType: deployerPrincipalType
  }
}

// deploy-agents.ps1 and `azd deploy geo-orchestrator` both write agent definitions to the project.
// Skipped where one role covers both, because the assignment name would collide with the one above.
resource deployerProjectManager 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerPrincipalId) && !empty(resolvedProjectManagerRole)) {
  scope: account
  name: guid(account.id, deployerPrincipalId, foundryProjectManager)
  properties: {
    principalId: deployerPrincipalId
    roleDefinitionId: foundryProjectManager
    principalType: deployerPrincipalType
  }
}

// An orchestrator the Agent Service hosts gets this access by virtue of running inside the project.
// One running anywhere else has to be granted it: it calls the model, and resolves each specialist
// by name against this account.
resource orchestratorFoundryUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(orchestratorPrincipalId)) {
  scope: account
  name: guid(account.id, orchestratorPrincipalId, foundryUser)
  properties: {
    principalId: orchestratorPrincipalId
    roleDefinitionId: foundryUser
    principalType: 'ServicePrincipal'
  }
}

output accountName string = account.name
output projectName string = project.name
output accountPrincipalId string = account.identity.principalId
output projectEndpoint string = 'https://${account.name}.services.ai.${resolvedAiServicesDomain}/api/projects/${project.name}'

// What deploy-agents.ps1 and invoke-agent.ps1 ask the CLI for a token against.
output tokenAudience string = 'https://ai.${resolvedAiServicesDomain}'
output orchestratorDeploymentName string = orchestratorModel.name
output specialistDeploymentName string = specialistModel.name
