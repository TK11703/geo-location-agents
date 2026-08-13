// Backend for the geo API: storage, Flex Consumption plan, App Insights, and the function app.
// The function app already exists, so this template is written to match it and update in place.

@description('Azure region for all resources in this group.')
param location string

param functionAppName string
param storageAccountName string
param hostingPlanName string
param appInsightsName string

@description('Existing Log Analytics workspace backing App Insights. Lives outside this resource group.')
param logAnalyticsWorkspaceId string

@description('Blob container holding the deployment package. Created by Flex Consumption on first publish.')
param deploymentContainerName string

@description('Container for rendered map images. The app also creates this at runtime.')
param mapImageContainerName string = 'map-images'

param mapImageUrlMinutes int = 15
param azureMapsEndpoint string = 'https://atlas.microsoft.com'
param elevationEndpoint string = 'https://epqs.nationalmap.gov/v1/json'

@description('Contact string the National Weather Service requires on every request.')
param nwsUserAgent string

@secure()
param azureMapsSubscriptionKey string

@description('azd matches this to the service in azure.yaml when deploying code.')
param serviceName string = 'api'

param tags object = {}

var storageBlobDataContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    // The host and the deployment package still authenticate with a connection string.
    allowSharedKeyAccess: true
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: deploymentContainerName
}

resource mapImageContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: mapImageContainerName
}

resource hostingPlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: hostingPlanName
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

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalyticsWorkspaceId
  }
}

var storageConnectionString = 'DefaultEndpointsProtocol=https;AccountName=${storage.name};AccountKey=${storage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: functionAppName
  location: location
  tags: union(tags, { 'azd-service-name': serviceName })
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: hostingPlan.id
    httpsOnly: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}${deploymentContainerName}'
          authentication: {
            type: 'StorageAccountConnectionString'
            storageAccountConnectionStringName: 'DEPLOYMENT_STORAGE_CONNECTION_STRING'
          }
        }
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
      scaleAndConcurrency: {
        instanceMemoryMB: 2048
        maximumInstanceCount: 100
      }
    }
    siteConfig: {
      // Flex Consumption takes runtime and version from functionAppConfig, and rejects the
      // FUNCTIONS_WORKER_RUNTIME / FUNCTIONS_EXTENSION_VERSION settings older plans require.
      appSettings: [
        {
          name: 'AzureWebJobsStorage'
          value: storageConnectionString
        }
        {
          name: 'DEPLOYMENT_STORAGE_CONNECTION_STRING'
          value: storageConnectionString
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
        {
          name: 'AzureMaps__Endpoint'
          value: azureMapsEndpoint
        }
        {
          name: 'AzureMaps__SubscriptionKey'
          value: azureMapsSubscriptionKey
        }
        {
          name: 'Elevation__Endpoint'
          value: elevationEndpoint
        }
        {
          name: 'Nws__UserAgent'
          value: nwsUserAgent
        }
        {
          name: 'Storage__MapImageContainer'
          value: mapImageContainerName
        }
        {
          name: 'Storage__MapImageUrlMinutes'
          value: string(mapImageUrlMinutes)
        }
        {
          // Presence of this switches the app from a connection string to managed identity,
          // which is what makes the returned image URLs user-delegation SAS rather than account-key SAS.
          name: 'Storage__MapImageServiceUri'
          value: storage.properties.primaryEndpoints.blob
        }
      ]
    }
  }
}

// Covers generateUserDelegationKey, so no separate Storage Blob Delegator assignment is needed.
resource blobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, functionApp.id, storageBlobDataContributor)
  properties: {
    principalId: functionApp.identity.principalId
    roleDefinitionId: storageBlobDataContributor
    principalType: 'ServicePrincipal'
  }
}

output functionAppName string = functionApp.name
output functionAppPrincipalId string = functionApp.identity.principalId
output functionAppApiUrl string = 'https://${functionApp.properties.defaultHostName}/api'
