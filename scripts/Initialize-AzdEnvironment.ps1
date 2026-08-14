<#
.SYNOPSIS
    Prepares the active azd environment for `azd provision`.

.DESCRIPTION
    Run as the preprovision hook. It does the two things the Bicep template cannot:

      1. Creates the Entra app registration whose App ID URI the gateway validates. The Foundry
         managed identity requests a token for that audience and API Management checks it, so the
         app and its service principal must exist in the tenant before the policy is deployed.
         Entra objects are not ARM resources, so this cannot live in Bicep.

      2. Fails before anything is provisioned when a required value is missing, rather than 40
         minutes in when API Management is already half built.

    Rerunning is safe. An existing app registration is reused; nothing is recreated.

    Requires the Entra Application Developer role (or better) the first time it runs in a tenant.
    Where that is not available, create the app registration by hand and set GEO_API_AUDIENCE, and
    this script will leave it alone.

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
    "{0,-22} {1}" -f $Name, $Value
}

$envName = $config['AZURE_ENV_NAME']

# Values with no sensible default. Named individually so the message says which one is missing.
$required = @{
    NWS_USER_AGENT       = "the contact string the National Weather Service requires, for example 'ERDC.Agents (you@example.com)'"
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

if (-not [string]::IsNullOrWhiteSpace($config['GEO_API_AUDIENCE'])) {
    "GEO_API_AUDIENCE      already set to $($config['GEO_API_AUDIENCE']); leaving the app registration alone"
    return
}

$account = az account show 2>$null | ConvertFrom-Json
if (-not $account) { throw 'Not signed in to the Azure CLI. Run az login.' }

$displayName = "geo-api-$envName"

$appId = az ad app list --display-name $displayName --query "[0].appId" -o tsv
if ([string]::IsNullOrWhiteSpace($appId)) {
    # The App ID URI has to contain the app id, which does not exist until the app does.
    $appId = az ad app create --display-name $displayName --sign-in-audience AzureADMyOrg --query appId -o tsv
    if ([string]::IsNullOrWhiteSpace($appId)) { throw "Could not create the app registration '$displayName'. This needs the Entra Application Developer role." }

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

Set-AzdValue GEO_API_APP_ID $appId
Set-AzdValue GEO_API_AUDIENCE "api://$appId"
