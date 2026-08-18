<#
.SYNOPSIS
    Invokes a Foundry prompt agent directly and prints its raw response.

.DESCRIPTION
    Bypasses the orchestrator so a specialist's structured report can be inspected exactly as the
    orchestrator receives it. Use this to tell a specialist that omitted something from an
    orchestrator that dropped it.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AgentName,
    [Parameter(Mandatory)][string]$Message
)

$ErrorActionPreference = 'Stop'

$config = & (Join-Path $PSScriptRoot '..\scripts\Get-AzdConfig.ps1') -Require FOUNDRY_PROJECT_ENDPOINT

# Derived from the target cloud by the Bicep; older environments predate the output.
$audience = if ($config.FOUNDRY_TOKEN_AUDIENCE) { $config.FOUNDRY_TOKEN_AUDIENCE } else { 'https://ai.azure.com' }

$token = (az account get-access-token --scope "$audience/.default" --query accessToken -o tsv)
if (-not $token) { throw "Could not acquire a token for $audience." }

$body = @{
    agent_reference = @{ type = 'agent_reference'; name = $AgentName }
    input           = $Message
} | ConvertTo-Json -Depth 10

# The /v1 path rejects an api-version query parameter.
$uri = "$($config.FOUNDRY_PROJECT_ENDPOINT)/openai/v1/responses"

try {
    $response = Invoke-RestMethod -Method Post -Uri $uri -Body $body -ContentType 'application/json' `
        -Headers @{ Authorization = "Bearer $token" }
}
catch {
    Write-Host "REQUEST FAILED: $($_.Exception.Message)"
    if ($_.ErrorDetails.Message) { Write-Host $_.ErrorDetails.Message }
    throw
}

# The agent's structured report is the text content of the assistant output item.
foreach ($item in $response.output) {
    foreach ($content in $item.content) {
        if ($content.text) {
            Write-Host '--- raw specialist report ---'
            Write-Host $content.text
        }
    }
}
