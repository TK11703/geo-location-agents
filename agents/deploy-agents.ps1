<#
.SYNOPSIS
    Creates or updates the four specialist prompt agents in Microsoft Foundry.

.DESCRIPTION
    Each agent is assembled from files in this repo so the deployed agent cannot drift from source:
      specs/<domain>.json          -> the OpenAPI tool spec, embedded verbatim
      agents/<name>.md             -> domain instructions
      agents/_output-contract.md   -> shared reporting contract, appended to every agent
      agents/report-schema.json    -> shared structured response format

    Auth is managed identity: the Foundry account's system-assigned MI requests a token for
    GEO_API_AUDIENCE, APIM validates it and injects the function host key. No secrets live here.

    Target project, model, audience, and gateway URL all come from the active azd environment, so
    this script is identical across environments.

.EXAMPLE
    ./agents/deploy-agents.ps1
    ./agents/deploy-agents.ps1 -Only weather-specialist
#>
[CmdletBinding()]
param(
    [string]$ApiVersion = 'v1',
    [string[]]$Only
)

$ErrorActionPreference = 'Stop'

$config = & (Join-Path $PSScriptRoot '..\scripts\Get-AzdConfig.ps1') `
    -Require FOUNDRY_PROJECT_ENDPOINT, SPECIALIST_MODEL, GEO_API_AUDIENCE, GEO_API_BASE_URL

$agents = @(
    @{
        Name        = 'weather-specialist'
        Spec        = 'weather.json'
        Tool        = 'geo_weather'
        Description = 'Current weather conditions and active severe-weather alerts for an explicit latitude and longitude.'
    }
    @{
        Name        = 'terrain-specialist'
        Spec        = 'terrain.json'
        Tool        = 'geo_terrain'
        Description = 'Ground elevation and local terrain relief for an explicit latitude and longitude.'
    }
    @{
        Name        = 'mobility-specialist'
        Spec        = 'mobility.json'
        Tool        = 'geo_mobility'
        Description = 'Active traffic incidents near a point and vehicle routing between two explicit coordinates.'
    }
    @{
        Name        = 'location-specialist'
        Spec        = 'location.json'
        Tool        = 'geo_location'
        Description = 'Reverse geocoding and rendered map imagery for an explicit latitude and longitude.'
    }
)

if ($Only) { $agents = $agents | Where-Object { $Only -contains $_.Name } }
if (-not $agents) { throw "No agents matched -Only $($Only -join ',')" }

$repoRoot = Split-Path $PSScriptRoot -Parent
$contract = (Get-Content (Join-Path $PSScriptRoot '_output-contract.md') -Raw).TrimStart([char]0xFEFF)
$textFormat = Get-Content (Join-Path $PSScriptRoot 'report-schema.json') -Raw | ConvertFrom-Json

$token = az account get-access-token --resource https://ai.azure.com --query accessToken -o tsv
if (-not $token) { throw 'Could not acquire an ai.azure.com token. Run az login.' }
$headers = @{ Authorization = "Bearer $token" }

foreach ($a in $agents) {
    $specPath = Join-Path $repoRoot "specs/$($a.Spec)"
    $spec = Get-Content $specPath -Raw | ConvertFrom-Json

    # The specs carry no server URL, so the same file can target any environment.
    $spec | Add-Member -NotePropertyName servers `
        -NotePropertyValue @(@{ url = $config.GEO_API_BASE_URL }) -Force

    # Foundry rejects operationIds containing digits, and the failure surfaces only at invoke time.
    foreach ($path in $spec.paths.PSObject.Properties) {
        foreach ($op in $path.Value.PSObject.Properties) {
            $id = $op.Value.operationId
            if ($id -notmatch '^[A-Za-z_-]+$') { throw "$($a.Spec): operationId '$id' must contain only letters, hyphens, and underscores." }
        }
    }

    $instructions = (Get-Content (Join-Path $PSScriptRoot "$($a.Name).md") -Raw).TrimStart([char]0xFEFF)

    $definition = @{
        kind         = 'prompt'
        model        = $config.SPECIALIST_MODEL
        instructions = "$instructions`n`n$contract"
        temperature  = 0.2
        text         = @{ format = $textFormat }
        tools        = @(
            @{
                type    = 'openapi'
                openapi = @{
                    name        = $a.Tool
                    description = $a.Description
                    spec        = $spec
                    auth        = @{
                        type            = 'managed_identity'
                        security_scheme = @{ audience = $config.GEO_API_AUDIENCE }
                    }
                }
            }
        )
    }

    $body = @{ definition = $definition } | ConvertTo-Json -Depth 100 -Compress
    $url = "$($config.FOUNDRY_PROJECT_ENDPOINT)/agents/$($a.Name)/versions?api-version=$ApiVersion"
    $result = Invoke-RestMethod -Method Post -Uri $url -Headers $headers -ContentType 'application/json' -Body $body

    "{0,-22} -> version {1}" -f $a.Name, $result.version
}
