# Geo Chat Web

A browser front end for the orchestrator. A person signs in with their Entra account, types a
question, and the app posts it to the orchestrator's OpenAI Responses endpoint with that person's
own token.

It is an ASP.NET Core Blazor Server app styled with Bootstrap, deployed to a Linux App Service by
[New-Deployment.ps1](../scripts/New-Deployment.ps1).

## What it is authorized as

The bearer token is the signed-in user's, not the web tier's. API Management's orchestrator policy
authorizes whoever presents a token for the orchestrator API's audience, so the person at the
keyboard is the principal the gateway sees, and revoking their access revokes it here too. A web
tier holding one application identity for everyone would have flattened that distinction.

```
browser -> web app (Entra sign-in)  ->  APIM  ->  orchestrator App Service  ->  specialists
              user's own token, audience api://<orchestrator app id>
```

Nothing here holds a secret. The app registration users sign in with is a confidential client, and
it proves itself with a federated credential naming the App Service's user-assigned managed
identity, so the only credential in play is a token the platform mints. That credential and the
redirect URIs are created after provisioning, by
[Set-WebAppRegistration.ps1](Set-WebAppRegistration.ps1), because both name resources the provision
is what creates.

Under a Foundry-hosted orchestrator there is no gateway and no orchestrator app registration: the
app calls the Foundry agent endpoint directly, with a token for the Foundry data plane. Signing in
is the same; only the endpoint and the scope differ, and both are app settings. A user needs the
Azure AI User role on the Foundry account for that path, because there is no delegated scope to
grant instead.

## Each question stands alone

The orchestrator stores no conversation — `ask.ps1` and this app both post `store: false` — so a
question is answered cold every time. The exchanges on the page are a record of what was asked, not
context the model is given. Ask a follow-up as a complete question.

## Configuration

| Setting | Where it comes from |
|---------|---------------------|
| `AzureAd__Instance`, `AzureAd__TenantId`, `AzureAd__ClientId` | app settings from [web-appservice.bicep](../infra/modules/web-appservice.bicep) |
| `AzureAd__ClientCredentials__0__SourceType`, `__ManagedIdentityClientId` | same; this is what replaces a client secret |
| `Orchestrator__Endpoint` | the gateway's `/orchestrator/responses`, or the Foundry agent endpoint |
| `Orchestrator__Scope` | `api://<orchestrator app id>/user_impersonation`, or `https://ai.<domain>/.default` |
| `Orchestrator__TimeoutSeconds` | optional; 240 by default, against the gateway's 230-second limit |

## Running it locally

The deployed registration already carries `https://localhost:7114/signin-oidc`, so the same one
works for `dotnet run`. Local runs need a client credential of their own, because there is no
managed identity to federate with off Azure; keep it in user secrets rather than in a file:

```pwsh
$config = ./scripts/Get-AzdConfig.ps1
cd webapp/src/geo-chat-web

dotnet user-secrets set 'AzureAd:Instance' 'https://login.microsoftonline.com/'
dotnet user-secrets set 'AzureAd:TenantId' $config['AZURE_TENANT_ID']
dotnet user-secrets set 'AzureAd:ClientId' $config['WEB_APP_CLIENT_ID']
dotnet user-secrets set 'AzureAd:ClientSecret' '<a secret added to that app registration>'
dotnet user-secrets set 'Orchestrator:Endpoint' $config['WEB_APP_ORCHESTRATOR_ENDPOINT']
dotnet user-secrets set 'Orchestrator:Scope' $config['WEB_APP_ORCHESTRATOR_SCOPE']

dotnet run
```

The orchestrator it talks to is the deployed one either way: the gateway is on the public internet
and authorizes by token, so a local front end reaches it exactly as the deployed one does. Point
`Orchestrator:Endpoint` at `http://localhost:8088/responses` to use a locally running orchestrator
instead, which accepts any request and ignores the token.
