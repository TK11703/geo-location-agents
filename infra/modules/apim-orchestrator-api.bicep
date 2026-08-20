// Adds the orchestrator API to the APIM instance the geo API already lives on.
//
// Only deployed when the orchestrator is self-hosted. A Foundry-hosted agent is reached through
// Foundry's own endpoint, which already validates a token, so there is nothing for a gateway to add.
//
// The shared named values (tenant id, login endpoint, v1 issuer) are declared by apim-geo-api.bicep,
// which is why main.bicep orders this module after it.

@description('Name of the existing APIM instance in this resource group.')
param apimServiceName string

param apiId string
param apiPath string

@description('Backend the gateway forwards to: the orchestrator App Service root.')
param orchestratorUrl string

@minLength(1)
@description('App ID URI of the app registration representing this API. Separate from the geo API audience so a token for one cannot be used against the other.')
param orchestratorApiAudience string

resource apim 'Microsoft.ApiManagement/service@2022-08-01' existing = {
  name: apimServiceName
}

resource audienceValue 'Microsoft.ApiManagement/service/namedValues@2022-08-01' = {
  parent: apim
  name: 'orchestrator-api-audience'
  properties: {
    displayName: 'orchestrator-api-audience'
    value: orchestratorApiAudience
    secret: false
  }
}

resource orchestratorApi 'Microsoft.ApiManagement/service/apis@2022-08-01' = {
  parent: apim
  name: apiId
  properties: {
    displayName: 'Geo-Location Orchestrator'
    description: 'OpenAI Responses endpoint of the orchestrator agent, which resolves a place and consults the specialists.'
    path: apiPath
    protocols: [
      'https'
    ]
    serviceUrl: orchestratorUrl
    // Callers present an Entra token instead; a subscription key would be a second shared secret.
    subscriptionRequired: false
    subscriptionKeyParameterNames: {
      header: 'Ocp-Apim-Subscription-Key'
      query: 'subscription-key'
    }
  }
}

// Hand-declared rather than imported: the agent host serves one route, and the Responses request
// body is defined by the OpenAI protocol rather than by anything in this repo.
resource createResponse 'Microsoft.ApiManagement/service/apis/operations@2022-08-01' = {
  parent: orchestratorApi
  name: 'create-response'
  properties: {
    displayName: 'Create response'
    description: 'Posts a message to the orchestrator and returns its answer.'
    method: 'POST'
    urlTemplate: '/responses'
  }
}

resource orchestratorApiPolicy 'Microsoft.ApiManagement/service/apis/policies@2022-08-01' = {
  parent: orchestratorApi
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: loadTextContent('../../apim/orchestrator-api.policy.xml')
  }
  dependsOn: [
    audienceValue
  ]
}

output gatewayApiUrl string = '${apim.properties.gatewayUrl}/${apiPath}'
