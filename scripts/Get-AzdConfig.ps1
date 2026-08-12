<#
.SYNOPSIS
    Returns the values of the repo's active azd environment as a hashtable.

.DESCRIPTION
    Every deployment script reads its environment-specific values from here, so retargeting the repo
    at a different subscription, APIM instance, or Foundry project means switching azd environments
    rather than editing scripts.

    Pass -Require to fail before any Azure call is made rather than partway through a deployment.

.EXAMPLE
    $config = & ./scripts/Get-AzdConfig.ps1 -Require APIM_SERVICE_NAME, APIM_RESOURCE_GROUP
#>
[CmdletBinding()]
param(
    [string[]]$Require = @()
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent

$raw = azd env get-values --cwd $repoRoot --output json 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Could not read an azd environment from $repoRoot. Create one with 'azd env new <name>' or pick one with 'azd env select <name>'.`n$raw"
}

$config = @{}
foreach ($property in ($raw | ConvertFrom-Json).PSObject.Properties) {
    $config[$property.Name] = $property.Value
}

$missing = @($Require | Where-Object { [string]::IsNullOrWhiteSpace($config[$_]) })
if ($missing.Count -gt 0) {
    throw ("azd environment '{0}' is missing {1}. Set each with: azd env set <KEY> <value>" -f $config['AZURE_ENV_NAME'], ($missing -join ', '))
}

$config
