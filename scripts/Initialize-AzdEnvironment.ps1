<#
.SYNOPSIS
    Prepares the active azd environment for `azd provision`.

.DESCRIPTION
    Run as the preprovision hook. It does the two things the Bicep template cannot:

      1. Creates the two Entra app registrations whose App ID URIs the gateway validates: one for
         the geo API the specialists call, and one for the orchestrator API that fronts the
         orchestrator when it is self-hosted. Each has its own audience so a token minted for one
         cannot be replayed against the other. Entra objects are not ARM resources, so this cannot
         live in Bicep. The orchestrator app also exposes a user_impersonation scope and
         pre-authorizes the Azure CLI, so a signed-in user can call it without an admin consenting
         to the API first.

      2. Mints the salt that keeps this environment's resource names, and so the Foundry
         subdomain, distinct from any previous build of the same environment name.

      3. Fails before anything is provisioned when a required value is missing, rather than 40
         minutes in when API Management is already half built.

    Rerunning is safe. An existing app registration is reused; nothing is recreated.

    Requires the Entra Application Developer role (or better) the first time it runs in a tenant.
    Where that is not available, create the app registrations by hand and set GEO_API_AUDIENCE and
    ORCHESTRATOR_API_AUDIENCE, and this script will leave them alone.

.EXAMPLE
    ./scripts/Initialize-AzdEnvironment.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
$config = & (Join-Path $PSScriptRoot 'Get-AzdConfig.ps1')

function Set-AzdValue {
    param([string]$Name, [string]$Value)
    azd env set $Name $Value --cwd $repoRoot | Out-Null
    "{0,-26} {1}" -f $Name, $Value
}

function New-ApiAppRegistration {
    param([string]$DisplayName)

    $appId = az ad app list --display-name $DisplayName --query "[0].appId" -o tsv
    if ([string]::IsNullOrWhiteSpace($appId)) {
        # The App ID URI has to contain the app id, which does not exist until the app does.
        $appId = az ad app create --display-name $DisplayName --sign-in-audience AzureADMyOrg --query appId -o tsv
        if ([string]::IsNullOrWhiteSpace($appId)) { throw "Could not create the app registration '$DisplayName'. This needs the Entra Application Developer role." }

        # Directory replication means the new app is not always readable on the very next call.
        $set = $false
        foreach ($attempt in 1..5) {
            az ad app update --id $appId --identifier-uris "api://$appId" 2>$null
            if ($LASTEXITCODE -eq 0) { $set = $true; break }
            Start-Sleep -Seconds ($attempt * 3)
        }
        if (-not $set) { throw "Created app $appId but could not set its identifier URI. Set it to api://$appId and rerun." }
    }

    # Without a service principal, Entra will not issue a token for this audience to anything.
    $spObjectId = az ad sp list --filter "appId eq '$appId'" --query "[0].id" -o tsv
    if ([string]::IsNullOrWhiteSpace($spObjectId)) {
        az ad sp create --id $appId | Out-Null
    }

    $appId
}

function Set-AzureCliPreAuthorization {
    param([string]$AppId)

    # First-party client id of the Azure CLI. Microsoft's own app ids are the same in every cloud.
    $azureCliAppId = '04b07795-8ddb-461a-bbee-02f9e1bf7b46'

    $api = az ad app show --id $AppId --query api -o json 2>$null | ConvertFrom-Json
    if (-not $api) {
        Write-Warning "Could not read app $AppId. ask.ps1 -Deployed will not be able to get a token for it until user_impersonation is exposed by hand."
        return
    }

    $scope = $api.oauth2PermissionScopes | Where-Object { $_.value -eq 'user_impersonation' }
    $preAuthorized = $api.preAuthorizedApplications | Where-Object { $_.appId -eq $azureCliAppId }
    if ($scope -and $scope.id -in $preAuthorized.delegatedPermissionIds) {
        "  $AppId already exposes user_impersonation to the Azure CLI"
        return
    }

    # Reused where one already exists: a scope id is what consent is recorded against, so minting a
    # new one silently revokes every grant made against the old.
    $scopeId = if ($scope) { $scope.id } else { [guid]::NewGuid().ToString() }

    $body = @{
        api = @{
            oauth2PermissionScopes = @(
                @{
                    id                      = $scopeId
                    value                   = 'user_impersonation'
                    type                    = 'User'
                    isEnabled               = $true
                    adminConsentDisplayName = 'Access the geo-location orchestrator'
                    adminConsentDescription = 'Allows the application to call the orchestrator API on behalf of the signed-in user.'
                    userConsentDisplayName  = 'Access the geo-location orchestrator'
                    userConsentDescription  = 'Allows the app to call the orchestrator API on your behalf.'
                }
            )
        }
    }

    $preAuth = @{
        api = @{
            preAuthorizedApplications = @(
                @{
                    appId                  = $azureCliAppId
                    delegatedPermissionIds = @($scopeId)
                }
            )
        }
    }

    # Patched through Graph rather than `az ad app update --set`, because the value is a nested
    # object and passing it as a quoted argument is at the mercy of the shell. The endpoint differs
    # per cloud, so it is read rather than assumed.
    $graphEndpoint = (az cloud show --query endpoints.microsoftGraphResourceId -o tsv).TrimEnd('/')
    $objectId = az ad app show --id $AppId --query id -o tsv
    $url = "$graphEndpoint/v1.0/applications/$objectId"

    $granted = $true
    $lastError = ''

    # Two calls, because Graph rejects a pre-authorization naming a scope id that the same request is
    # only just introducing.
    foreach ($payload in $body, $preAuth) {
        $bodyFile = New-TemporaryFile
        $ok = $false

        try {
            Set-Content -Path $bodyFile -Value ($payload | ConvertTo-Json -Depth 6) -Encoding utf8

            # Directory replication means a freshly created app is not always writable on the next call.
            foreach ($attempt in 1..5) {
                $lastError = az rest --method PATCH --url $url `
                    --headers 'Content-Type=application/json' --body "@$($bodyFile.FullName)" 2>&1 | Out-String
                if ($LASTEXITCODE -eq 0) { $ok = $true; break }
                Start-Sleep -Seconds ($attempt * 3)
            }
        }
        finally {
            Remove-Item $bodyFile -Force -ErrorAction SilentlyContinue
        }

        if (-not $ok) { $granted = $false; break }
    }

    # A warning rather than a throw: the deployment itself is unaffected, and only the convenience of
    # asking the Azure CLI for a token is lost.
    if ($granted) { "  $AppId now exposes user_impersonation to the Azure CLI" }
    else {
        Write-Warning "Could not expose user_impersonation on app $AppId. Deployment is unaffected, but ask.ps1 -Deployed will fail with AADSTS65001 until the scope is added and the Azure CLI ($azureCliAppId) is pre-authorized.`n$($lastError.Trim())"
    }
}

$envName = $config['AZURE_ENV_NAME']

# Values with no sensible default. Named individually so the message says which one is missing.
$required = @{
    NWS_USER_AGENT       = "the contact string the National Weather Service requires, for example 'GeoLocation (you@example.com)'"
    APIM_PUBLISHER_EMAIL = 'the address API Management sends service notifications to'
}

$missing = @($required.Keys | Where-Object { [string]::IsNullOrWhiteSpace($config[$_]) } | Sort-Object)
if ($missing.Count -gt 0) {
    $lines = $missing | ForEach-Object { "  azd env set $_ '<$($required[$_])>'" }
    throw "azd environment '$envName' is missing $($missing -join ', '). Set each:`n$($lines -join "`n")"
}

# The Bicep default cannot apply here: azd substitutes an empty string for an unset variable, and
# an empty string overrides a default rather than falling back to it.
if ([string]::IsNullOrWhiteSpace($config['AZURE_RESOURCE_GROUP'])) {
    Set-AzdValue AZURE_RESOURCE_GROUP "rg-$envName"
}

# Foundry keeps agent records, and the Entra agent identities they point at, keyed to the account's
# subdomain, and purging the account does not clear them. A rebuild that lands on the same subdomain
# inherits agent versions whose identities the teardown deleted, and every invocation then fails
# with 'Agent Identity Blueprint has been deleted or disabled'. Minted once and kept for the life of
# the environment, so reprovisioning is still idempotent; a torn-down environment gets a new one.
if ([string]::IsNullOrWhiteSpace($config['AZD_RESOURCE_TOKEN_SALT'])) {
    Set-AzdValue AZD_RESOURCE_TOKEN_SALT ([guid]::NewGuid().ToString('N').Substring(0, 8))
}

# Both are created regardless of where the orchestrator ends up running. Which host applies is
# resolved by main.bicep from the cloud being deployed into, and duplicating that map here to skip
# one app registration would be a second place for it to drift.
#
# Only the orchestrator exposes a delegated scope. The geo API is called by managed identities using
# client credentials, which need no consent; the orchestrator is called by a person holding an Azure
# CLI token, which does.
$apps = [ordered]@{
    GEO_API          = @{ DisplayName = "geo-api-$envName"; ExposeToAzureCli = $false }
    ORCHESTRATOR_API = @{ DisplayName = "geo-orchestrator-$envName"; ExposeToAzureCli = $true }
}

$account = az account show 2>$null | ConvertFrom-Json
if (-not $account) { throw 'Not signed in to the Azure CLI. Run az login.' }

foreach ($key in $apps.Keys) {
    $app = $apps[$key]

    if ([string]::IsNullOrWhiteSpace($config["${key}_AUDIENCE"])) {
        $appId = New-ApiAppRegistration -DisplayName $app.DisplayName
        Set-AzdValue "${key}_APP_ID" $appId
        Set-AzdValue "${key}_AUDIENCE" "api://$appId"
    }
    else {
        "{0,-26} already set to {1}; leaving the app registration alone" -f "${key}_AUDIENCE", $config["${key}_AUDIENCE"]
        $appId = $config["${key}_APP_ID"]
        if (-not $appId) { $appId = $config["${key}_AUDIENCE"] -replace '^api://', '' }
    }

    # Checked on every run rather than only the one that creates the app, because an audience supplied
    # by hand arrives with no scope on it.
    if ($app.ExposeToAzureCli -and $appId) { Set-AzureCliPreAuthorization -AppId $appId }
}
