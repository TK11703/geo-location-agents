<#
.SYNOPSIS
    Posts a single request to the orchestrator in a brand new conversation.

.DESCRIPTION
    'azd ai agent invoke --new-session' resets the session but keeps the same conversation, so the
    agent can answer a repeated question from conversation history without calling any tool. Each
    call here is unconditionally cold, which is what a test needs.

    Targets the local dev host by default, or the deployed agent with -Deployed.

.EXAMPLE
    ./orchestrator/ask.ps1 -Message 'Conditions at 47.6062, -122.3321?'
    ./orchestrator/ask.ps1 -Message 'Conditions at 51.5072, -0.1276?' -Deployed
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Message,
    [int]$Port = 8088,
    [switch]$Deployed
)

$ErrorActionPreference = 'Stop'

$body = @{ input = $Message; store = $false } | ConvertTo-Json -Depth 5

if ($Deployed) {
    $config = & (Join-Path $PSScriptRoot '..\scripts\Get-AzdConfig.ps1') -Require FOUNDRY_PROJECT_ENDPOINT

    # A hosted agent is reached through its own endpoint; the project-level responses route is for
    # prompt agents and rejects this one outright.
    $uri = "$($config.FOUNDRY_PROJECT_ENDPOINT)/agents/geo-orchestrator/endpoint/protocols/openai/responses?api-version=v1"

    $token = az account get-access-token --resource https://ai.azure.com --query accessToken -o tsv
    if (-not $token) { throw 'Could not acquire an ai.azure.com token. Run az login.' }

    $response = Invoke-RestMethod -Method Post -Uri $uri -Headers @{ Authorization = "Bearer $token" } `
        -Body $body -ContentType 'application/json'
}
else {
    $response = Invoke-RestMethod -Method Post -Uri "http://localhost:$Port/responses" `
        -Body $body -ContentType 'application/json'
}

foreach ($item in $response.output) {
    foreach ($content in $item.content) {
        if ($content.text) { Write-Host $content.text }
    }
}
