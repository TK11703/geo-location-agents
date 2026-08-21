// Function app on a Dedicated (App Service) plan, for clouds that offer neither Flex Consumption
// nor shared key access to storage. Dedicated is the only Functions tier with no content share:
// the package lands on the platform's own filesystem, so the host reaches the storage account
// through a managed identity and no account key exists anywhere in the configuration.

param name string
param planName string
param location string
param tags object = {}

@description('azd matches this to the service in azure.yaml when deploying code.')
param serviceName string

@description('B1 is the cheapest tier offering Always On. Autoscale starts at P1v3.')
param sku string = 'B1'

param linuxFxVersion string = 'DOTNET-ISOLATED|10.0'
param functionsWorkerRuntime string = 'dotnet-isolated'
param functionsExtensionVersion string = '~4'

@description('Identity the host authenticates as. Created before this module so its storage roles exist by first start.')
param identityResourceId string
param identityClientId string

@description('Data plane the host keeps its locks and scale state in. Blob, queue, and table are all required.')
param storageBlobUri string
param storageQueueUri string
param storageTableUri string

@description('Application configuration shared with the Flex variant. Host settings are added below.')
@secure()
param appSettings object

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  tags: tags
  kind: 'linux'
  sku: {
    name: sku
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
  // No system-assigned identity: two identities would leave DefaultAzureCredential in the app with
  // no way to tell which one to present.
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityResourceId}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: linuxFxVersion
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      appSettings: concat(
        [
          // Identity-based form of AzureWebJobsStorage, which replaces the connection string
          // outright rather than supplementing it.
          {
            name: 'AzureWebJobsStorage__blobServiceUri'
            value: storageBlobUri
          }
          {
            name: 'AzureWebJobsStorage__queueServiceUri'
            value: storageQueueUri
          }
          {
            name: 'AzureWebJobsStorage__tableServiceUri'
            value: storageTableUri
          }
          {
            name: 'AzureWebJobsStorage__credential'
            value: 'managedidentity'
          }
          {
            name: 'AzureWebJobsStorage__clientId'
            value: identityClientId
          }
          {
            name: 'FUNCTIONS_WORKER_RUNTIME'
            value: functionsWorkerRuntime
          }
          {
            name: 'FUNCTIONS_EXTENSION_VERSION'
            value: functionsExtensionVersion
          }
          // What DefaultAzureCredential in the app reads to pick the user-assigned identity.
          {
            name: 'AZURE_CLIENT_ID'
            value: identityClientId
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
output defaultHostName string = site.properties.defaultHostName
