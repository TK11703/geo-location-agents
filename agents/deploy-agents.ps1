<#
.SYNOPSIS
    Creates or updates the specialist prompt agents in Microsoft Foundry.

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

    Rerunning is safe. Foundry returns the existing version when a submitted definition matches the
    current one, so identical redeploys do not accumulate versions; this script reads the version
    before and after publishing to report which agents actually moved.

.PARAMETER ReadyTimeoutMinutes
    How long to wait for the project to start answering on the Agents endpoint before giving up.
    On a freshly provisioned account this lags the ARM deployment by a long way, and until it
    catches up every request returns 404 'Project not found'.

.EXAMPLE
    ./agents/deploy-agents.ps1
    ./agents/deploy-agents.ps1 -Only weather-specialist
#>
[CmdletBinding()]
param(
    [string]$ApiVersion = 'v1',
    [string[]]$Only,

    [ValidateRange(0, 240)]
    [int]$ReadyTimeoutMinutes = 60
)

$ErrorActionPreference = 'Stop'

$config = & (Join-Path $PSScriptRoot '..\scripts\Get-AzdConfig.ps1') `
    -Require FOUNDRY_PROJECT_ENDPOINT, SPECIALIST_MODEL, GEO_API_AUDIENCE, GEO_API_BASE_URL

$agents = @(
    @{
        Name        = 'place-resolver'
        Spec        = 'place.json'
        Tool        = 'geo_place'
        Description = 'Turns a written place name or street address into candidate latitude and longitude coordinates.'
    }
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

# Written into the environment by the Bicep, which derives it from the target cloud. Environments
# provisioned before that output existed fall back to the commercial audience.
$audience = if ($config.FOUNDRY_TOKEN_AUDIENCE) { $config.FOUNDRY_TOKEN_AUDIENCE } else { 'https://ai.azure.com' }

function Get-FoundryHeaders {
    $token = az account get-access-token --resource $audience --query accessToken -o tsv
    if (-not $token) { throw "Could not acquire a token for $audience. Run az login." }
    @{ Authorization = "Bearer $token" }
}

$headers = Get-FoundryHeaders

# A new account's project is not routable on the Agents endpoint the moment provisioning finishes.
# ARM reports Succeeded well before the Agents backend picks the project up, and until it does every
# request here answers 404 'Project not found'. Waiting is the difference between this running as an
# azd postdeploy hook and having to rerun it by hand later.
$deadline = (Get-Date).AddMinutes($ReadyTimeoutMinutes)
while ($true) {
    try {
        Invoke-RestMethod -Uri "$($config.FOUNDRY_PROJECT_ENDPOINT)/agents?api-version=$ApiVersion" -Headers $headers | Out-Null
        break
    }
    catch {
        if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw }
        if ((Get-Date) -ge $deadline) {
            throw "$($config.FOUNDRY_PROJECT_ENDPOINT) still reports 'Project not found' after $ReadyTimeoutMinutes minutes. Check that the account and project exist and that FOUNDRY_PROJECT_ENDPOINT points at them."
        }
        "  {0}  waiting for the project to become available on the Agents endpoint" -f (Get-Date -Format HH:mm:ss)
        Start-Sleep -Seconds 30
        # Refreshed each attempt so a long wait cannot outlive the token.
        $headers = Get-FoundryHeaders
    }
}

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

    $url = "$($config.FOUNDRY_PROJECT_ENDPOINT)/agents/$($a.Name)/versions?api-version=$ApiVersion"

    # Foundry itself decides whether a submission differs from the current version, which is a
    # sounder comparison than anything reconstructed here. All this needs is the version to compare
    # the result against. An agent that does not exist yet returns an empty list rather than a 404,
    # so this stays null and the first publish is reported as a new version.
    $versions = Invoke-RestMethod -Uri $url -Headers $headers
    $before = ($versions.data | Sort-Object { [int]$_.version } -Descending | Select-Object -First 1).version

    $body = @{ definition = $definition } | ConvertTo-Json -Depth 100 -Compress
    $result = Invoke-RestMethod -Method Post -Uri $url -Headers $headers -ContentType 'application/json' -Body $body

    if ($result.version -eq $before) { "{0,-22}    unchanged (version {1})" -f $a.Name, $result.version }
    else { "{0,-22} -> version {1}" -f $a.Name, $result.version }
}
