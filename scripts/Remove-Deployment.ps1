<#
.SYNOPSIS
    Removes everything New-Deployment.ps1 creates: the Azure resources, the Entra app registration,
    and both local azd environments.

.DESCRIPTION
    `azd down` is most of a teardown but not all of one. Five things sit outside what it removes:

      1. The Foundry Agents capability host. This one has to go first, before `azd down` rather
         than after it: the record lives in the Agents backend keyed by account name, so purging
         the Cognitive Services account does not take it with it. `azd down` reports success while
         the host is still deleting, and the next deployment then fails within a second on
         "Capability Host agents is currently in non creating, retry after its complete" because
         the regenerated account name collides with the one still going away.
      2. The Entra app registration the preprovision hook creates, and the agent identity blueprints
         and principals Foundry creates for each published agent. Entra objects are not ARM
         resources, so no deployment owns them and `azd down` never sees them. The agent identities
         are the ones that accumulate: a fresh set of three objects per agent, every deployment.
      3. The orchestrator's azd environment. Its hosted agent lives inside the Foundry project the
         root project owns, so the agent goes away with the resource group; what is left behind is
         a local environment naming an account that no longer exists, which the next deployment
         would otherwise reuse.
      4. Soft-deleted Cognitive Services accounts and API Management instances. `azd down --purge`
         normally clears both, and this verifies it did.
      5. The root azd environment itself, which still holds the resource ids of what was just
         deleted.

    Purging matters here rather than being tidiness. Resource names derive from
    uniqueString(subscription, environment name, location), so redeploying the same environment
    name into the same region regenerates the same names and collides with the soft-deleted ones,
    and a soft-deleted Foundry account goes on holding its model-deployment quota until it is gone.

    Rerunning is safe. Anything already deleted is reported and skipped, so this can be used to
    finish a teardown that failed partway through.

.PARAMETER EnvironmentName
    Root azd environment to remove. Defaults to whichever one azd currently has selected.

.PARAMETER KeepAppRegistration
    Leaves the Entra app registration in place. Use this when the app was supplied through
    New-Deployment.ps1 -GeoApiAudience rather than created by the preprovision hook, or when the
    same app is shared by another environment.

.PARAMETER KeepEnvironmentFiles
    Deletes the Azure resources but keeps both local azd environments, so a redeploy reuses the
    same settings instead of asking for them again.

.PARAMETER CapabilityHostTimeoutMinutes
    How long to wait for the Agents capability host to finish deleting before giving up and
    tearing down the rest anyway. Deletion has been observed taking anywhere from three minutes to
    over an hour.

.PARAMETER Force
    Skips the confirmation prompt.

.EXAMPLE
    ./scripts/Remove-Deployment.ps1

.EXAMPLE
    # Preview what would be deleted without touching anything.
    ./scripts/Remove-Deployment.ps1 -WhatIf

.EXAMPLE
    # Tear down an environment whose local .azure folder is already gone.
    ./scripts/Remove-Deployment.ps1 -EnvironmentName erdc-agents-dev -SubscriptionId <subscription-id> -Force
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [string]$EnvironmentName,
    [string]$OrchestratorEnvironmentName = 'geo-orchestrator-dev',

    # Both are read from the azd environment when it still exists. They are only needed by hand
    # when tearing down resources whose local environment has already been deleted.
    [string]$TenantId,
    [string]$SubscriptionId,
    [string]$ResourceGroupName,

    # Must match the cloud the environment was deployed into, or nothing here will be found.
    [ValidateSet('AzureCloud', 'AzureUSGovernment', 'AzureChinaCloud')]
    [string]$Cloud = 'AzureCloud',

    [switch]$KeepAppRegistration,
    [switch]$KeepEnvironmentFiles,

    [ValidateRange(0, 240)]
    [int]$CapabilityHostTimeoutMinutes = 45,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# capabilityHosts is preview-only and is not listed by `az provider show`, so the version is fixed
# here rather than discovered.
$capabilityHostApiVersion = '2025-10-01-preview'

$repoRoot = Split-Path $PSScriptRoot -Parent
$orchestratorRoot = Join-Path $repoRoot 'orchestrator'

if ($Force) { $ConfirmPreference = 'None' }

function Write-Step {
    param([string]$Message)
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Invoke-Native {
    param([string]$Command, [string[]]$Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
}

function Get-AzdEnvValues {
    param([string]$Name, [string]$Cwd)
    $raw = azd env get-values --environment $Name --cwd $Cwd --output json 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $raw) { return @{} }

    $values = @{}
    foreach ($property in ($raw | ConvertFrom-Json).PSObject.Properties) { $values[$property.Name] = $property.Value }
    $values
}

function Get-AzdEnvList {
    param([string]$Cwd)
    @(azd env list --cwd $Cwd --output json 2>$null | ConvertFrom-Json)
}

function Test-AzdEnv {
    param([string]$Name, [string]$Cwd)
    [bool]((Get-AzdEnvList -Cwd $Cwd) | Where-Object { $_.Name -eq $Name })
}

function Get-CapabilityHost {
    param([string]$AccountUrl)
    $raw = az rest --method get --url "$AccountUrl/capabilityHosts?api-version=$capabilityHostApiVersion" --output json 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $raw) { return @() }
    @(($raw | ConvertFrom-Json).value)
}

function Remove-CapabilityHost {
    param([string]$AccountUrl, [string]$AccountName, [int]$TimeoutMinutes)

    $existing = Get-CapabilityHost -AccountUrl $AccountUrl
    if ($existing.Count -eq 0) {
        "  '$AccountName' has no capability host"
        return
    }

    foreach ($capabilityHost in $existing) {
        "  deleting '$($capabilityHost.name)' on '$AccountName' (currently $($capabilityHost.properties.provisioningState))"
        az rest --method delete --url "$AccountUrl/capabilityHosts/$($capabilityHost.name)?api-version=$capabilityHostApiVersion" --output none 2>$null
    }

    if ($TimeoutMinutes -eq 0) {
        "  not waiting for deletion (-CapabilityHostTimeoutMinutes 0)"
        return
    }

    # The delete is asynchronous and nothing downstream reports on it, so poll until the collection
    # is empty. Until it is, the account name stays unusable for a redeploy.
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 30
        $remaining = Get-CapabilityHost -AccountUrl $AccountUrl
        if ($remaining.Count -eq 0) {
            "  capability host deleted"
            return
        }
        "    $(Get-Date -Format HH:mm:ss)  $($remaining[0].properties.provisioningState)"
    }

    Write-Warning "Capability host on '$AccountName' was still deleting after $TimeoutMinutes minutes. Continuing with the teardown, but redeploying this environment name into this region will fail until it clears."
}

foreach ($tool in 'azd', 'az') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "'$tool' is not on PATH. The Azure Developer CLI and the Azure CLI are both required."
    }
}

Write-Step 'Resolving what to remove'

# The name comes from the environment list rather than from `azd env get-values`, because that
# command invents an environment named after the directory when none is selected. Taking the name
# from it would aim a destructive script at a resource group nobody ever deployed.
if (-not $EnvironmentName) {
    $environments = Get-AzdEnvList -Cwd $repoRoot
    $EnvironmentName = ($environments | Where-Object { $_.IsDefault } | Select-Object -First 1).Name
    if (-not $EnvironmentName -and $environments.Count -eq 1) { $EnvironmentName = $environments[0].Name }
}

if (-not $EnvironmentName) {
    throw "No azd environment is selected in $repoRoot and -EnvironmentName was not given. Pass the name of the environment to remove."
}

# Read the environment before anything is deleted: it is where the generated resource names live,
# and they cannot be recomputed afterwards without repeating the uniqueString the Bicep uses.
$config = if (Test-AzdEnv -Name $EnvironmentName -Cwd $repoRoot) { Get-AzdEnvValues -Name $EnvironmentName -Cwd $repoRoot } else { @{} }

if (-not $SubscriptionId) { $SubscriptionId = $config['AZURE_SUBSCRIPTION_ID'] }
if (-not $TenantId) { $TenantId = $config['AZURE_TENANT_ID'] }
if (-not $ResourceGroupName) { $ResourceGroupName = $config['AZURE_RESOURCE_GROUP'] }
if (-not $ResourceGroupName) { $ResourceGroupName = "rg-$EnvironmentName" }

# Fall back to the naming convention in main.bicep and the preprovision hook when the environment
# is already gone. The prefixes are fixed; only the resource token varies.
$foundryAccountName = $config['FOUNDRY_ACCOUNT_NAME']
$foundryProjectName = $config['FOUNDRY_PROJECT_NAME']
$apimServiceName = $config['APIM_SERVICE_NAME']
$appRegistrationName = "geo-api-$EnvironmentName"
$appId = $config['GEO_API_APP_ID']

if ((az cloud show --query name -o tsv 2>$null) -ne $Cloud) { Invoke-Native az @('cloud', 'set', '--name', $Cloud) }
Invoke-Native azd @('config', 'set', 'cloud.name', $Cloud)

$account = az account show 2>$null | ConvertFrom-Json
if (-not $account) {
    if ($TenantId) { Invoke-Native az @('login', '--tenant', $TenantId) } else { Invoke-Native az @('login') }
}
if ($SubscriptionId) { Invoke-Native az @('account', 'set', '--subscription', $SubscriptionId) }
$account = az account show | ConvertFrom-Json
if (-not $SubscriptionId) { $SubscriptionId = $account.id }

$groupExists = (az group exists --name $ResourceGroupName) -eq 'true'

"  subscription      $($account.name) ($SubscriptionId)"
"  environment       $EnvironmentName"
"  resource group    $ResourceGroupName$(if (-not $groupExists) { '  (already gone)' })"
"  app registration  $(if ($KeepAppRegistration) { 'kept (-KeepAppRegistration)' } else { $appRegistrationName })"
"  local azd envs    $(if ($KeepEnvironmentFiles) { 'kept (-KeepEnvironmentFiles)' } else { "$EnvironmentName, $OrchestratorEnvironmentName" })"

$target = "resource group '$ResourceGroupName' in subscription $SubscriptionId"
if (-not $PSCmdlet.ShouldProcess($target, 'Permanently delete every resource, and purge the soft-deleted ones')) {
    Write-Step 'Nothing was deleted.'
    return
}

Write-Step 'Removing the Foundry capability host'

if (-not $groupExists) {
    "  resource group '$ResourceGroupName' does not exist; nothing to remove"
}
else {
    # Discovered rather than assumed when the environment is gone, because the capability host has
    # to be addressed by the account's generated name.
    if (-not $foundryAccountName) {
        $foundryAccountName = az resource list --resource-group $ResourceGroupName --resource-type 'Microsoft.CognitiveServices/accounts' --query '[0].name' -o tsv
    }

    if ([string]::IsNullOrWhiteSpace($foundryAccountName)) {
        "  no Foundry account in '$ResourceGroupName'"
    }
    else {
        $armEndpoint = (az cloud show --query endpoints.resourceManager -o tsv).TrimEnd('/')
        $accountUrl = "$armEndpoint/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName/providers/Microsoft.CognitiveServices/accounts/$foundryAccountName"
        Remove-CapabilityHost -AccountUrl $accountUrl -AccountName $foundryAccountName -TimeoutMinutes $CapabilityHostTimeoutMinutes
    }
}

Write-Step 'Deleting Azure resources'

if (-not $groupExists) {
    "  resource group '$ResourceGroupName' does not exist; nothing to delete"
}
elseif (Test-AzdEnv -Name $EnvironmentName -Cwd $repoRoot) {
    # --purge is what clears the soft-delete state of API Management and the Foundry account, which
    # otherwise keep both names reserved and the model quota allocated.
    Invoke-Native azd @('down', '--cwd', $repoRoot, '--environment', $EnvironmentName, '--force', '--purge')
}
else {
    # No local environment to drive azd with. The template is subscription scoped and creates the
    # group, so deleting the group is equivalent for everything inside it.
    "  no local azd environment '$EnvironmentName'; deleting the resource group directly"
    Invoke-Native az @('group', 'delete', '--name', $ResourceGroupName, '--yes')
}

Write-Step 'Purging soft-deleted resources'

# Only ever purges resources belonging to this environment. A subscription can hold soft-deleted
# accounts from unrelated work, and those are somebody else's to restore.
$purged = 0

$deletedAccounts = az cognitiveservices account list-deleted --output json | ConvertFrom-Json
foreach ($deleted in $deletedAccounts) {
    $matchesEnvironment = if ($foundryAccountName) { $deleted.name -eq $foundryAccountName } else { $deleted.name -like "aif-$EnvironmentName-*" }
    if (-not $matchesEnvironment) { continue }

    # The purge is addressed by the group and region the account was deleted from, both of which
    # survive only in the deleted account's own id.
    $segments = $deleted.id -split '/'
    $location = $segments[[array]::IndexOf($segments, 'locations') + 1]
    $group = $segments[[array]::IndexOf($segments, 'resourceGroups') + 1]

    Invoke-Native az @('cognitiveservices', 'account', 'purge', '--name', $deleted.name, '--location', $location, '--resource-group', $group)
    "  purged Foundry account $($deleted.name)"
    $purged++
}

$deletedApim = az apim deletedservice list --output json 2>$null | ConvertFrom-Json
foreach ($deleted in $deletedApim) {
    $matchesEnvironment = if ($apimServiceName) { $deleted.name -eq $apimServiceName } else { $deleted.name -like "apim-$EnvironmentName-*" }
    if (-not $matchesEnvironment) { continue }

    Invoke-Native az @('apim', 'deletedservice', 'purge', '--service-name', $deleted.name, '--location', $deleted.location)
    "  purged API Management $($deleted.name)"
    $purged++
}

if ($purged -eq 0) { "  nothing belonging to '$EnvironmentName' was in a soft-deleted state" }

if (-not $KeepAppRegistration) {
    Write-Step "Deleting the Entra app registration '$appRegistrationName'"

    if (-not $appId) { $appId = az ad app list --display-name $appRegistrationName --query '[0].appId' -o tsv }

    if ([string]::IsNullOrWhiteSpace($appId)) {
        "  no app registration named '$appRegistrationName'; nothing to delete"
    }
    else {
        # Deleting the application usually takes its service principal with it, but not reliably
        # enough to leave a stale principal holding role assignments in the tenant.
        $spObjectId = az ad sp list --filter "appId eq '$appId'" --query '[0].id' -o tsv
        if (-not [string]::IsNullOrWhiteSpace($spObjectId)) {
            az ad sp delete --id $appId
            "  deleted service principal $spObjectId"
        }

        az ad app delete --id $appId
        "  deleted app registration $appId"
    }
}

Write-Step 'Deleting Entra agent identities'

# Publishing an agent creates an Entra blueprint application, its principal, and a per-agent
# identity principal. None of them are ARM resources, so deleting the resource group leaves all
# three behind, and another set appears for every agent the next time an environment is deployed.
# Matching on the account and project names keeps this to identities this project created.
$agentIdentityPrefix = if ($foundryAccountName -and $foundryProjectName) { "$foundryAccountName-$foundryProjectName-" } else { "aif-$EnvironmentName-" }
$agentIdentityFilter = { $_.displayName -like "$agentIdentityPrefix*AgentIdentity*" }

$agentPrincipals = @(az ad sp list --all --output json | ConvertFrom-Json | Where-Object $agentIdentityFilter)
$agentApps = @(az ad app list --all --output json | ConvertFrom-Json | Where-Object $agentIdentityFilter)

# Principals first: deleting an application takes its own principal with it, but the per-agent
# identities have no application to be removed by.
foreach ($principal in $agentPrincipals) {
    az ad sp delete --id $principal.id 2>$null
    if ($LASTEXITCODE -eq 0) { "  deleted principal $($principal.displayName)" }
    else { Write-Warning "Could not delete service principal '$($principal.displayName)'." }
}

foreach ($app in $agentApps) {
    az ad app delete --id $app.id 2>$null
    if ($LASTEXITCODE -eq 0) { "  deleted blueprint $($app.displayName)" }
    else { Write-Warning "Could not delete app registration '$($app.displayName)'." }
}

if ($agentPrincipals.Count -eq 0 -and $agentApps.Count -eq 0) { "  no agent identities matching '$agentIdentityPrefix*'" }

if (-not $KeepEnvironmentFiles) {
    Write-Step 'Removing local azd environments'

    foreach ($env in @(
            @{ Name = $EnvironmentName; Cwd = $repoRoot },
            @{ Name = $OrchestratorEnvironmentName; Cwd = $orchestratorRoot })) {

        if (Test-AzdEnv -Name $env.Name -Cwd $env.Cwd) {
            Invoke-Native azd @('env', 'remove', $env.Name, '--cwd', $env.Cwd, '--force')
            "  removed '$($env.Name)'"
        }
        else {
            "  '$($env.Name)' does not exist locally"
        }
    }
}

Write-Step 'Done'

if ((az group exists --name $ResourceGroupName) -eq 'true') {
    Write-Warning "Resource group '$ResourceGroupName' still exists. Deletion may still be running; rerun this script to confirm it finished."
}
else {
    "  resource group '$ResourceGroupName' is gone"
}
''
'Redeploy with:'
"  ./scripts/New-Deployment.ps1 -TenantId <tenant-id> -SubscriptionId <subscription-id> ``"
"      -NwsUserAgent '<contact string>' -ApimPublisherEmail <address>"
