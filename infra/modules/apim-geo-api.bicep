// Adds the geo API to an APIM instance that already exists and hosts other APIs.
// Only the API and the named values its policy needs are declared here -- never the service itself.

@description('Name of the existing APIM instance in this resource group.')
param apimServiceName string

param apiId string
param apiPath string

@description('Resource group holding the function app, so the host key can be read at deploy time.')
param functionAppResourceGroup string
param functionAppName string

@description('Backend the gateway forwards to, including the /api route prefix.')
param functionAppApiUrl string

param entraTenantId string
param geoApiAudience string

@description('Object id of the Foundry managed identity. The policy pins the token oid claim to this.')
param foundryMiPrincipalId string

resource apim 'Microsoft.ApiManagement/service@2022-08-01' existing = {
  name: apimServiceName
}

// Reading the key here removes the ordering problem of having to publish the function app,
// copy its key out by hand, and only then configure the gateway.
var functionHostKey = listKeys(
  resourceId(subscription().subscriptionId, functionAppResourceGroup, 'Microsoft.Web/sites/host', functionAppName, 'default'),
  '2024-04-01'
).functionKeys.default

resource tenantIdValue 'Microsoft.ApiManagement/service/namedValues@2022-08-01' = {
  parent: apim
  name: 'entra-tenant-id'
  properties: {
    displayName: 'entra-tenant-id'
    value: entraTenantId
    secret: false
  }
}

resource audienceValue 'Microsoft.ApiManagement/service/namedValues@2022-08-01' = {
  parent: apim
  name: 'geo-api-audience'
  properties: {
    displayName: 'geo-api-audience'
    value: geoApiAudience
    secret: false
  }
}

resource foundryOidValue 'Microsoft.ApiManagement/service/namedValues@2022-08-01' = {
  parent: apim
  name: 'foundry-mi-oid'
  properties: {
    displayName: 'foundry-mi-oid'
    value: foundryMiPrincipalId
    secret: false
  }
}

resource hostKeyValue 'Microsoft.ApiManagement/service/namedValues@2022-08-01' = {
  parent: apim
  name: 'function-host-key'
  properties: {
    displayName: 'function-host-key'
    value: functionHostKey
    secret: true
  }
}

resource geoApi 'Microsoft.ApiManagement/service/apis@2022-08-01' = {
  parent: apim
  name: apiId
  properties: {
    displayName: 'ERDC Agents Geo API'
    description: 'Weather, terrain, mobility, and location endpoints consumed by Foundry specialist agents.'
    path: apiPath
    protocols: [
      'https'
    ]
    serviceUrl: functionAppApiUrl
    // Callers present an Entra token instead; a subscription key would be a second shared secret.
    subscriptionRequired: false
    subscriptionKeyParameterNames: {
      header: 'Ocp-Apim-Subscription-Key'
      query: 'subscription-key'
    }
    format: 'openapi+json'
    value: loadTextContent('../../apim/geo-api.openapi.json')
  }
}

resource geoApiPolicy 'Microsoft.ApiManagement/service/apis/policies@2022-08-01' = {
  parent: geoApi
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: loadTextContent('../../apim/geo-api.policy.xml')
  }
  dependsOn: [
    tenantIdValue
    audienceValue
    foundryOidValue
    hostKeyValue
  ]
}

output gatewayApiUrl string = '${apim.properties.gatewayUrl}/${apiPath}'
