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

var foundryUser = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '53ca6127-db72-4b80-b1b0-d745d6d5456d')
var foundryProjectManager = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'eadc314b-1a2d-4efa-be10-5d325db5065e')

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
resource deployerProjectManager 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerPrincipalId)) {
  scope: account
  name: guid(account.id, deployerPrincipalId, foundryProjectManager)
  properties: {
    principalId: deployerPrincipalId
    roleDefinitionId: foundryProjectManager
    principalType: deployerPrincipalType
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
