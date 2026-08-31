<#
.SYNOPSIS
    Finishes the web front end's Entra app registration once its App Service exists.

.DESCRIPTION
    Two parts of that registration cannot be created before provisioning, because both name a
    resource the provision is what creates:

      1. The redirect URIs, which are the deployed app's own hostname. Entra rejects a sign-in whose
         reply URL is not registered, so this has to be right before anyone can sign in.
      2. A federated identity credential naming the app's user-assigned managed identity. It is what
         lets a confidential client prove itself with a token the platform mints rather than with a
         client secret, so nothing anywhere in this deployment holds one.

    Rerunning is safe: existing redirect URIs are merged rather than replaced, and a federated
    credential whose subject already matches is left alone.

.EXAMPLE
    ./webapp/Set-WebAppRegistration.ps1
#>
[CmdletBinding()]
param(
    # Defaults come from the azd environment, which is where the provision wrote them.
    [string]$ClientId,
    [string]$WebAppUrl,
    [string]$IdentityPrincipalId
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent

if (-not $ClientId -or -not $WebAppUrl -or -not $IdentityPrincipalId) {
    $config = & (Join-Path $repoRoot 'scripts/Get-AzdConfig.ps1') -Require WEB_APP_CLIENT_ID, WEB_APP_URL, WEB_APP_IDENTITY_PRINCIPAL_ID
    if (-not $ClientId) { $ClientId = $config['WEB_APP_CLIENT_ID'] }
    if (-not $WebAppUrl) { $WebAppUrl = $config['WEB_APP_URL'] }
    if (-not $IdentityPrincipalId) { $IdentityPrincipalId = $config['WEB_APP_IDENTITY_PRINCIPAL_ID'] }
}

$account = az account show 2>$null | ConvertFrom-Json
if (-not $account) { throw 'Not signed in to the Azure CLI. Run az login.' }

$baseUrl = $WebAppUrl.TrimEnd('/')
$redirectUri = "$baseUrl/signin-oidc"
$logoutUrl = "$baseUrl/signout-callback-oidc"

$app = az ad app show --id $ClientId -o json | ConvertFrom-Json
if (-not $app) { throw "No app registration with client id $ClientId. Run the preprovision hook first." }

# Localhost is kept alongside the deployed URL so the same registration serves `dotnet run`, which
# is the only way to exercise the app without deploying it.
$existing = @($app.web.redirectUris)
$wanted = @($redirectUri, 'https://localhost:7114/signin-oidc')
$uris = @($existing + $wanted | Select-Object -Unique)

if (($uris.Count -ne $existing.Count) -or ($app.web.logoutUrl -ne $logoutUrl)) {
    az ad app update --id $ClientId --web-redirect-uris @uris | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not set the redirect URIs on app $ClientId." }

    # Not settable through `az ad app update`, so it goes through Graph like the other nested values.
    $graphEndpoint = (az cloud show --query endpoints.microsoftGraphResourceId -o tsv).TrimEnd('/')
    $bodyFile = New-TemporaryFile
    try {
        Set-Content -Path $bodyFile -Value (@{ web = @{ logoutUrl = $logoutUrl } } | ConvertTo-Json -Depth 4) -Encoding utf8
        az rest --method PATCH --url "$graphEndpoint/v1.0/applications/$($app.id)" `
            --headers 'Content-Type=application/json' --body "@$($bodyFile.FullName)" | Out-Null
    }
    finally {
        Remove-Item $bodyFile -Force -ErrorAction SilentlyContinue
    }

    "  redirect URIs  $($uris -join ', ')"
}
else {
    "  redirect URIs already registered"
}

# The issuer is the tenant's own, which differs per cloud, so it is read rather than assumed. The
# subject is the managed identity's object id: Entra will only accept an assertion signed for that
# identity as proof that this client is who it says it is.
$authority = (az cloud show --query endpoints.activeDirectory -o tsv).TrimEnd('/')
$issuer = "$authority/$($account.tenantId)/v2.0"
$credentialName = 'geo-web-managed-identity'

$credentials = az ad app federated-credential list --id $ClientId -o json | ConvertFrom-Json
$existingCredential = $credentials | Where-Object { $_.name -eq $credentialName }

if ($existingCredential -and $existingCredential.subject -eq $IdentityPrincipalId -and $existingCredential.issuer -eq $issuer) {
    "  federated credential already names $IdentityPrincipalId"
    return
}

# A credential left over from a previous deployment names an identity that no longer exists, and
# Entra does not allow the subject to be changed in place.
if ($existingCredential) {
    az ad app federated-credential delete --id $ClientId --federated-credential-id $existingCredential.id | Out-Null
}

$parameters = @{
    name      = $credentialName
    issuer    = $issuer
    subject   = $IdentityPrincipalId
    audiences = @('api://AzureADTokenExchange')
} | ConvertTo-Json -Depth 4 -Compress

$parametersFile = New-TemporaryFile
try {
    Set-Content -Path $parametersFile -Value $parameters -Encoding utf8
    az ad app federated-credential create --id $ClientId --parameters "@$($parametersFile.FullName)" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not create the federated credential on app $ClientId. Without it the web app cannot sign anyone in." }
}
finally {
    Remove-Item $parametersFile -Force -ErrorAction SilentlyContinue
}

"  federated credential now names $IdentityPrincipalId"
