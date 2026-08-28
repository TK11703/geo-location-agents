// Browser front end for the orchestrator. An ordinary ASP.NET Core app on Linux App Service that
// signs a person in with Entra and calls the orchestrator with that person's own token, so the
// gateway authorizes whoever is at the keyboard rather than the web tier acting for everyone.
//
// Nothing here holds a secret. The app registration the app signs users in with proves itself with
// a federated credential bound to this identity, so the only credential in play is a token the
// platform mints; webapp/Set-WebAppRegistration.ps1 creates that federated credential, because
// Entra objects are not ARM resources.

param name string
param planName string
param location string
param tags object = {}

@description('B1 is the cheapest tier that offers Always On, which a Blazor Server circuit waiting on a slow agent needs.')
param sku string = 'B1'

@description('Name of the user-assigned identity the app runs as. Its object id is the subject of the app registration federated credential.')
param identityName string

@description('Client id of the app registration users sign in with.')
param authClientId string

@description('Entra authority for this cloud, with a trailing slash.')
param entraLoginEndpoint string = environment().authentication.loginEndpoint

param entraTenantId string = subscription().tenantId

@description('Absolute URL of the orchestrator Responses route: the gateway when self-hosted, the Foundry agent endpoint otherwise.')
param orchestratorEndpoint string

@description('Delegated scope a token has to be minted for to reach that endpoint.')
param orchestratorScope string

@secure()
@description('App Insights the rest of the system reports to, so a question and the calls it caused land in one workspace.')
param appInsightsConnectionString string = ''

param linuxFxVersion string = 'DOTNETCORE|10.0'

@description('Entry point. Named explicitly so the platform does not have to guess which assembly in the package is the host.')
param startupCommand string = 'dotnet geo-chat-web.dll'

// Created here rather than passed in, because nothing outside this module refers to it: the only
// role it holds is one Entra grants through a federated credential, which is not an ARM resource.
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
  tags: tags
}

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
  tags: tags
  kind: 'app,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: linuxFxVersion
      appCommandLine: startupCommand
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
      // Blazor Server holds the page open on a WebSocket; without one it falls back to long polling
      // and a two-minute answer is far likelier to be dropped in between.
      webSocketsEnabled: true

      appSettings: concat(
        [
          {
            name: 'AzureAd__Instance'
            value: entraLoginEndpoint
          }
          {
            name: 'AzureAd__TenantId'
            value: entraTenantId
          }
          {
            name: 'AzureAd__ClientId'
            value: authClientId
          }
          // Microsoft.Identity.Web asks the platform for a token and presents that as the client
          // assertion, which is why no client secret exists for this app anywhere.
          {
            name: 'AzureAd__ClientCredentials__0__SourceType'
            value: 'SignedAssertionFromManagedIdentity'
          }
          {
            name: 'AzureAd__ClientCredentials__0__ManagedIdentityClientId'
            value: identity.properties.clientId
          }
          {
            name: 'AZURE_CLIENT_ID'
            value: identity.properties.clientId
          }
          {
            name: 'Orchestrator__Endpoint'
            value: orchestratorEndpoint
          }
          {
            name: 'Orchestrator__Scope'
            value: orchestratorScope
          }
        ],
        empty(appInsightsConnectionString)
          ? []
          : [
              {
                name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
                value: appInsightsConnectionString
              }
              {
                name: 'ApplicationInsightsAgent_EXTENSION_VERSION'
                value: '~3'
              }
            ]
      )
    }
  }
}

output name string = site.name
output url string = 'https://${site.properties.defaultHostName}'
output identityPrincipalId string = identity.properties.principalId
