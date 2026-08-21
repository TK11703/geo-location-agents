// Function app on a Flex Consumption plan. Runtime, deployment source, and scaling are declared in
// functionAppConfig, which no other plan accepts.

param name string
param planName string
param location string
param tags object = {}

@description('azd matches this to the service in azure.yaml when deploying code.')
param serviceName string

@description('Blob container holding the deployment package, as a full URI.')
param deploymentStorageUri string

@secure()
param storageConnectionString string

@description('Application configuration shared with the Elastic Premium variant. Host settings are added below.')
@secure()
param appSettings object

param runtimeName string = 'dotnet-isolated'
param runtimeVersion string = '10.0'
param instanceMemoryMB int = 2048
param maximumInstanceCount int = 100

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  tags: tags
  kind: 'functionapp'
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  properties: {
    reserved: true
  }
}

resource site 'Microsoft.Web/sites@2024-04-01' = {
  name: name
  location: location
  tags: union(tags, { 'azd-service-name': serviceName })
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: deploymentStorageUri
          authentication: {
            type: 'StorageAccountConnectionString'
            storageAccountConnectionStringName: 'DEPLOYMENT_STORAGE_CONNECTION_STRING'
          }
        }
      }
      runtime: {
        name: runtimeName
        version: runtimeVersion
      }
      scaleAndConcurrency: {
        instanceMemoryMB: instanceMemoryMB
        maximumInstanceCount: maximumInstanceCount
      }
    }
    siteConfig: {
      // Flex takes runtime and version from functionAppConfig above, and rejects the
      // FUNCTIONS_WORKER_RUNTIME / FUNCTIONS_EXTENSION_VERSION settings older plans require.
      appSettings: concat(
        [
          {
            name: 'AzureWebJobsStorage'
            value: storageConnectionString
          }
          {
            name: 'DEPLOYMENT_STORAGE_CONNECTION_STRING'
            value: storageConnectionString
          }
        ],
        map(items(appSettings), setting => {
          name: setting.key
          value: setting.value
        })
      )
    }
  }
}

output name string = site.name
output principalId string = site.identity.principalId
output defaultHostName string = site.properties.defaultHostName
