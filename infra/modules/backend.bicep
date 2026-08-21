// Backend for the geo API: Log Analytics, storage, App Insights, Azure Maps, and the function app.
// The plan and the site live in a per-tier module, because Flex Consumption and Dedicated configure
// the same app through different property bags rather than through different values.

@description('Azure region for all resources in this group.')
param location string

param functionAppName string
param storageAccountName string
param hostingPlanName string
param appInsightsName string
param logAnalyticsWorkspaceName string

@description('Days of log retention on the workspace backing App Insights.')
param logRetentionDays int = 30

@description('Blob container holding the deployment package. Created by Flex Consumption on first publish.')
param deploymentContainerName string

@description('Container for rendered map images. The app also creates this at runtime.')
param mapImageContainerName string = 'map-images'

param mapImageUrlMinutes int = 15

@description('Azure Maps data plane. Supplied by main.bicep, which resolves it for the target cloud.')
param azureMapsEndpoint string

param elevationEndpoint string = 'https://epqs.nationalmap.gov/v1/json'
param nwsEndpoint string = 'https://api.weather.gov'

@description('Contact string the National Weather Service requires on every request.')
param nwsUserAgent string

param mapsAccountName string

@description('Hosting tier for the function app. Supplied by main.bicep, which resolves it for the target cloud.')
@allowed([
  'FlexConsumption'
  'Dedicated'
])
param functionPlanTier string = 'FlexConsumption'

@description('App Service tier used when functionPlanTier is Dedicated.')
param functionAppServiceSku string = 'B1'

@description('Identity the Dedicated host runs as. Unused on Flex Consumption.')
param functionIdentityName string

@description('azd matches this to the service in azure.yaml when deploying code.')
param serviceName string = 'api'

param tags object = {}

var flexConsumption = functionPlanTier == 'FlexConsumption'

var storageBlobDataContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
var storageBlobDataOwner = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
var storageQueueDataContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
var storageTableDataContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3')

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsWorkspaceName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: logRetentionDays
  }
}

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
    // Only Flex Consumption needs this: its host and deployment package authenticate with a
    // connection string, and it has no identity-based equivalent for the deployment container.
    allowSharedKeyAccess: flexConsumption
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

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

// Gen1 (S0 and S1) is retired, so G2 is the only tier a new account can be created with.
// Maps accounts exist in six locations only, none of which is guaranteed to be this group's
// region, so this stays global rather than following `location`.
resource maps 'Microsoft.Maps/accounts@2023-06-01' = {
  name: mapsAccountName
  location: 'global'
  tags: tags
  sku: {
    name: 'G2'
  }
  kind: 'Gen2'
  properties: {
    disableLocalAuth: false
  }
}

var storageConnectionString = 'DefaultEndpointsProtocol=https;AccountName=${storage.name};AccountKey=${storage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'

// The app's own configuration, identical on either plan. Each module adds the host settings its
// plan requires on top of this.
var appSettings = {
  APPLICATIONINSIGHTS_CONNECTION_STRING: appInsights.properties.ConnectionString
  AzureMaps__Endpoint: azureMapsEndpoint
  AzureMaps__SubscriptionKey: maps.listKeys().primaryKey
  Elevation__Endpoint: elevationEndpoint
  Nws__Endpoint: nwsEndpoint
  Nws__UserAgent: nwsUserAgent
  Storage__MapImageContainer: mapImageContainerName
  Storage__MapImageUrlMinutes: string(mapImageUrlMinutes)
  // Presence of this switches the app from a connection string to managed identity, which is what
  // makes the returned image URLs user-delegation SAS rather than account-key SAS.
  Storage__MapImageServiceUri: storage.properties.primaryEndpoints.blob
}

// Exists before the site, because the host needs its storage roles at first start and a
// system-assigned identity cannot be granted anything until the site it belongs to exists.
resource functionIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = if (!flexConsumption) {
  name: functionIdentityName
  location: location
  tags: tags
}

// Blob Data Owner rather than Contributor: it carries generateUserDelegationKey, which the app
// needs to sign map image URLs, on top of the container access the host needs for its locks.
resource hostBlobOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!flexConsumption) {
  scope: storage
  name: guid(storage.id, functionIdentityName, storageBlobDataOwner)
  properties: {
    principalId: functionIdentity!.properties.principalId
    roleDefinitionId: storageBlobDataOwner
    principalType: 'ServicePrincipal'
  }
}

resource hostQueueContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!flexConsumption) {
  scope: storage
  name: guid(storage.id, functionIdentityName, storageQueueDataContributor)
  properties: {
    principalId: functionIdentity!.properties.principalId
    roleDefinitionId: storageQueueDataContributor
    principalType: 'ServicePrincipal'
  }
}

resource hostTableContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!flexConsumption) {
  scope: storage
  name: guid(storage.id, functionIdentityName, storageTableDataContributor)
  properties: {
    principalId: functionIdentity!.properties.principalId
    roleDefinitionId: storageTableDataContributor
    principalType: 'ServicePrincipal'
  }
}

module functionFlex 'function-flex.bicep' = if (flexConsumption) {
  name: 'function-flex'
  params: {
    name: functionAppName
    planName: hostingPlanName
    location: location
    tags: tags
    serviceName: serviceName
    deploymentStorageUri: '${storage.properties.primaryEndpoints.blob}${deploymentContainerName}'
    storageConnectionString: storageConnectionString
    appSettings: appSettings
  }
}

module functionDedicated 'function-dedicated.bicep' = if (!flexConsumption) {
  name: 'function-dedicated'
  params: {
    name: functionAppName
    planName: hostingPlanName
    location: location
    tags: tags
    serviceName: serviceName
    sku: functionAppServiceSku
    identityResourceId: functionIdentity!.id
    identityClientId: functionIdentity!.properties.clientId
    storageBlobUri: storage.properties.primaryEndpoints.blob
    storageQueueUri: storage.properties.primaryEndpoints.queue
    storageTableUri: storage.properties.primaryEndpoints.table
    appSettings: appSettings
  }
  dependsOn: [
    hostBlobOwner
    hostQueueContributor
    hostTableContributor
  ]
}

// Covers generateUserDelegationKey, so no separate Storage Blob Delegator assignment is needed.
// The id is derived rather than read from the module, because a resource name cannot depend on
// another deployment's output.
resource siteBlobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (flexConsumption) {
  scope: storage
  name: guid(storage.id, resourceId('Microsoft.Web/sites', functionAppName), storageBlobDataContributor)
  properties: {
    principalId: functionFlex!.outputs.principalId
    roleDefinitionId: storageBlobDataContributor
    principalType: 'ServicePrincipal'
  }
}

output functionAppName string = functionAppName
output functionAppPrincipalId string = flexConsumption ? functionFlex!.outputs.principalId : functionIdentity!.properties.principalId
output functionAppApiUrl string = 'https://${flexConsumption ? functionFlex!.outputs.defaultHostName : functionDedicated!.outputs.defaultHostName}/api'
output mapsAccountName string = maps.name
output logAnalyticsWorkspaceId string = logAnalytics.id

@secure()
output appInsightsConnectionString string = appInsights.properties.ConnectionString
