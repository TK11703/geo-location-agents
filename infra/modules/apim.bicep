// The API Management instance that fronts the geo API. Declared here rather than referenced,
// so the repo can be deployed into a subscription that has nothing in it yet.

param apimServiceName string
param location string

@description('Shown in the developer portal and used as the sender for service notifications.')
param publisherName string
param publisherEmail string

@description('Developer has no SLA and takes 30-45 minutes to provision, but is the cheapest tier that still supports the rate-limit-by-key policy this API applies.')
@allowed([
  'Developer'
  'Basicv2'
  'Standardv2'
  'Premium'
])
param sku string = 'Developer'

param tags object = {}

resource apim 'Microsoft.ApiManagement/service@2024-05-01' = {
  name: apimServiceName
  location: location
  tags: tags
  sku: {
    name: sku
    capacity: 1
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    publisherName: publisherName
    publisherEmail: publisherEmail
  }
}

output apimServiceName string = apim.name
output gatewayUrl string = apim.properties.gatewayUrl
