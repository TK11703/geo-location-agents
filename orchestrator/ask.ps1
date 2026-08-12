<#
.SYNOPSIS
    Posts a single request to the locally running orchestrator in a brand new conversation.

.DESCRIPTION
    'azd ai agent invoke --new-session' resets the session but keeps the same conversation, so the
    agent can answer a repeated question from conversation history without calling any tool. Each
    call here is unconditionally cold, which is what a test needs.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Message,
    [int]$Port = 8088
)

$ErrorActionPreference = 'Stop'

$body = @{ input = $Message; store = $false } | ConvertTo-Json -Depth 5

$response = Invoke-RestMethod -Method Post -Uri "http://localhost:$Port/responses" `
    -Body $body -ContentType 'application/json'

foreach ($item in $response.output) {
    foreach ($content in $item.content) {
        if ($content.text) { Write-Host $content.text }
    }
}
