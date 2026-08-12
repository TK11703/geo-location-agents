<#
.SYNOPSIS
    Applies geo-api.policy.xml to the APIM API named in the active azd environment.

.DESCRIPTION
    The policy carries no environment values of its own -- audience, tenant, Foundry MI object id,
    and the function host key are all APIM named values -- so this only has to locate the API.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$config = & (Join-Path $PSScriptRoot '..\scripts\Get-AzdConfig.ps1') `
    -Require AZURE_SUBSCRIPTION_ID, APIM_RESOURCE_GROUP, APIM_SERVICE_NAME, APIM_API_ID

$policyPath = Join-Path $PSScriptRoot 'geo-api.policy.xml'

$xml = (Get-Content $policyPath -Raw).TrimStart([char]0xFEFF)
$body = @{ properties = @{ format = 'rawxml'; value = $xml } } | ConvertTo-Json -Depth 5

# Invoke-RestMethod instead of `az rest`: APIM echoes the stored policy back with a
# UTF-8 BOM, which the az CLI cannot decode and reports as a fatal error even though
# the PUT succeeded. Invoke-RestMethod handles it and surfaces real failures as
# terminating errors, so a successful run actually means the policy was applied.
$token = az account get-access-token --resource https://management.azure.com --query accessToken -o tsv
if (-not $token) { throw 'Could not acquire a management.azure.com token. Run az login.' }

$url = 'https://management.azure.com/subscriptions/{0}/resourceGroups/{1}/providers/Microsoft.ApiManagement/service/{2}/apis/{3}/policies/policy?api-version=2022-08-01' -f `
    $config.AZURE_SUBSCRIPTION_ID, $config.APIM_RESOURCE_GROUP, $config.APIM_SERVICE_NAME, $config.APIM_API_ID

Invoke-RestMethod -Method Put -Uri $url -Headers @{ Authorization = "Bearer $token" } -ContentType 'application/json' -Body $body | Out-Null

"Applied $policyPath to $($config.APIM_API_ID) on $($config.APIM_SERVICE_NAME)"
