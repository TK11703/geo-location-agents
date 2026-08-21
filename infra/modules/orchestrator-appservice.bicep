// Runs the orchestrator as an ordinary ASP.NET Core app, for clouds where the Foundry Agent Service
// cannot host it. The binary is the same either way: AgentHost serves the OpenAI Responses protocol
// at /responses whatever it is running on, so only the address callers use changes.

param name string
param planName string
param location string
param tags object = {}

@description('B1 is the cheapest tier that offers Always On, which a fan-out agent behind a gateway timeout needs.')
param sku string = 'B1'

@description('User-assigned identity the app runs as. It is the one holding the Foundry data-plane role.')
param identityResourceId string
param identityClientId string

param foundryProjectEndpoint string
param modelDeploymentName string

@description('Foundry data-plane audience for this cloud. The client libraries hard-code the commercial one.')
param foundryTokenAudience string

@secure()
@description('App Insights the function app already reports to, so both halves of a request land in one workspace.')
param appInsightsConnectionString string = ''

@description('Object id of the gateway managed identity, the only caller allowed through.')
param callerPrincipalId string

@description('App registration representing this API. Its App ID URI is the audience tokens must carry.')
param authClientId string
param authAudience string

@description('Managed identity tokens are issued as v1, so this is the issuer the app validates against.')
param entraV1Issuer string = 'https://sts.windows.net/'

param linuxFxVersion string = 'DOTNETCORE|10.0'

@description('Port the app listens on. 8080 is what the Linux front end probes by default.')
param containerPort int = 8080

@description('Entry point. Named explicitly so the platform does not have to guess which assembly in the package is the host.')
param startupCommand string = 'dotnet geo-orchestrator.dll'

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
      '${identityResourceId}': {}
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

      appSettings: concat(
        [
          // AgentHost binds the port named here and overrides ASPNETCORE_URLS to do it, so this is
          // the only knob that moves it off its 8088 default onto the port the front end probes.
          {
            name: 'PORT'
            value: string(containerPort)
          }
          {
            name: 'WEBSITES_PORT'
            value: string(containerPort)
          }
          // DefaultAzureCredential probes the system-assigned identity, which this app does not have,
          // unless it is told which user-assigned one to use.
          {
            name: 'AZURE_CLIENT_ID'
            value: identityClientId
          }
          {
            name: 'FOUNDRY_PROJECT_ENDPOINT'
            value: foundryProjectEndpoint
          }
          {
            name: 'AZURE_AI_MODEL_DEPLOYMENT_NAME'
            value: modelDeploymentName
          }
          {
            name: 'FOUNDRY_TOKEN_AUDIENCE'
            value: foundryTokenAudience
          }
        ],
        // AgentHost wires its own telemetry off this one setting, which is why the app carries no
        // Application Insights package and needs no codeless attach.
        empty(appInsightsConnectionString)
          ? []
          : [
              {
                name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
                value: appInsightsConnectionString
              }
            ]
      )
    }
  }
}

// Authorization by caller identity rather than by source address. An address allowlist looked
// equivalent but silently degraded to open: the gateway's public IP is not exposed on every tier,
// and an unresolved address left the app reachable by anyone. This has no such failure mode, and it
// rejects unauthenticated requests before they reach the app.
resource auth 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: site
  name: 'authsettingsV2'
  properties: {
    platform: {
      enabled: true
    }
    globalValidation: {
      requireAuthentication: true
      unauthenticatedClientAction: 'Return401'
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          openIdIssuer: '${entraV1Issuer}${subscription().tenantId}/'
          clientId: authClientId
        }
        validation: {
          allowedAudiences: [
            authAudience
          ]
          defaultAuthorizationPolicy: {
            allowedPrincipals: {
              identities: [
                callerPrincipalId
              ]
            }
          }
        }
      }
    }
    login: {
      // Nothing here is a browser session, so there is no token worth storing.
      tokenStore: {
        enabled: false
      }
    }
  }
}

output name string = site.name
output url string = 'https://${site.properties.defaultHostName}'
