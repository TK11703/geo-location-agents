$ErrorActionPreference = 'Stop'

$rg = 'Holonet'
$apim = 'Holonet-APIM-Databank-8108'
$apiId = 'geo-api'
$policyPath = Join-Path $PSScriptRoot 'geo-api.policy.xml'
$sub = az account show --query id -o tsv

$xml = (Get-Content $policyPath -Raw).TrimStart([char]0xFEFF)
$body = @{ properties = @{ format = 'rawxml'; value = $xml } } | ConvertTo-Json -Depth 5

# Invoke-RestMethod instead of `az rest`: APIM echoes the stored policy back with a
# UTF-8 BOM, which the az CLI cannot decode and reports as a fatal error even though
# the PUT succeeded. Invoke-RestMethod handles it and surfaces real failures as
# terminating errors, so a successful run actually means the policy was applied.
$token = az account get-access-token --resource https://management.azure.com --query accessToken -o tsv
$url = "https://management.azure.com/subscriptions/$sub/resourceGroups/$rg/providers/Microsoft.ApiManagement/service/$apim/apis/$apiId/policies/policy?api-version=2022-08-01"
Invoke-RestMethod -Method Put -Uri $url -Headers @{ Authorization = "Bearer $token" } -ContentType 'application/json' -Body $body | Out-Null

"Applied $policyPath to $apiId"
