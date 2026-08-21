<#
.SYNOPSIS
    Removes everything New-Deployment.ps1 creates: the Azure resources, the Entra app registrations,
    and both local azd environments.

.DESCRIPTION
    `azd down` is most of a teardown but not all of one. Four things sit outside what it removes:

      1. The Entra app registrations the preprovision hook creates, and the agent identity blueprints
         and principals Foundry creates for each published agent. Entra objects are not ARM
         resources, so no deployment owns them and `azd down` never sees them. The agent identities
         are the ones that accumulate: a fresh set of three objects per agent, every deployment.
      2. The orchestrator's azd environment. Its hosted agent lives inside the Foundry project the
         root project owns, so the agent goes away with the resource group; what is left behind is
         a local environment naming an account that no longer exists, which the next deployment
         would otherwise reuse.
      3. Soft-deleted Cognitive Services accounts and API Management instances. `azd down --purge`
         normally clears both, and this verifies it did.
      4. The root azd environment itself, which still holds the resource ids of what was just
         deleted.

    Deleting the Foundry Agents capability host, which happens first, is now a compatibility path.
    Environments provisioned before the capability host was removed from infra/modules/foundry.bicep
    still have one, and it has to go before `azd down` rather than after: the record lives in the
    Agents backend keyed by account name, so purging the Cognitive Services account does not take it
    with it. Current deployments have none, and the step says so and moves on.

    Purging matters for quota rather than tidiness: a soft-deleted Foundry account goes on holding
    its model-deployment quota until it is gone. Colliding names are no longer the second reason
    they once were, because resource names derive from uniqueString(subscription, environment name,
    location, salt) and the salt is minted per environment and goes away with it. Under
    -KeepEnvironmentFiles the salt survives, so a redeploy does regenerate the same names and would
    collide with anything left soft-deleted.

    Rerunning is safe. Anything already deleted is reported and skipped, so this can be used to
    finish a teardown that failed partway through. It will also get that far: every step is best
    effort, because each one is cleaning up something already orphaned. A step that fails warns,
    the steps after it still run, and everything that failed is listed again at the end.

.PARAMETER EnvironmentName
    Root azd environment to remove. Defaults to whichever one azd currently has selected.

.PARAMETER KeepAppRegistration
    Leaves the Entra app registrations in place. Use this when an app was supplied through
    New-Deployment.ps1 -GeoApiAudience rather than created by the preprovision hook, or when the
    same app is shared by another environment.

.PARAMETER KeepEnvironmentFiles
    Deletes the Azure resources but keeps both local azd environments, so a redeploy reuses the
    same settings instead of asking for them again.

.PARAMETER CapabilityHostTimeoutMinutes
    How long to wait for the Agents capability host to finish deleting before giving up and
    tearing down the rest anyway. Deletion has been observed taking anywhere from three minutes to
    over an hour. Applies only to environments old enough to have a capability host.

.PARAMETER Force
    Skips the confirmation prompt.

.EXAMPLE
    ./scripts/Remove-Deployment.ps1

.EXAMPLE
    # Preview what would be deleted without touching anything.
    ./scripts/Remove-Deployment.ps1 -WhatIf

.EXAMPLE
    # Tear down an environment whose local .azure folder is already gone.
    ./scripts/Remove-Deployment.ps1 -EnvironmentName geo-agents-dev -SubscriptionId <subscription-id> -Force
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

# PowerShell 7.4 turns a non-zero native exit code into a terminating error by default. This script
# handles exit codes itself: Invoke-Native throws deliberately, and the probes below treat a
# non-zero exit as "not found" and carry on. Leaving it on would turn every probe into an abort.
$PSNativeCommandUseErrorActionPreference = $false

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

$teardownFailures = [System.Collections.Generic.List[string]]::new()

# Each deletion step is independent cleanup of something already orphaned, so a failure in one is a
# reason to warn rather than a reason to abandon the others.
function Invoke-BestEffort {
    param([string]$Description, [scriptblock]$Action, [string]$Hint)
    try {
        & $Action
    }
    catch {
        Write-Warning "$Description failed: $($_.Exception.Message)"
        if ($Hint) { Write-Warning "  $Hint" }
        $teardownFailures.Add($Description)
    }
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
$appRegistrations = [ordered]@{
    "geo-api-$EnvironmentName"          = $config['GEO_API_APP_ID']
    "geo-orchestrator-$EnvironmentName" = $config['ORCHESTRATOR_API_APP_ID']
}

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
"  app registration  $(if ($KeepAppRegistration) { 'kept (-KeepAppRegistration)' } else { $appRegistrations.Keys -join ', ' })"
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
        Invoke-BestEffort "Removing the capability host on '$foundryAccountName'" {
            Remove-CapabilityHost -AccountUrl $accountUrl -AccountName $foundryAccountName -TimeoutMinutes $CapabilityHostTimeoutMinutes
        }
    }
}

Write-Step 'Deleting Azure resources'

$failuresBeforeDelete = $teardownFailures.Count

if (-not $groupExists) {
    "  resource group '$ResourceGroupName' does not exist; nothing to delete"
}
elseif (Test-AzdEnv -Name $EnvironmentName -Cwd $repoRoot) {
    # --purge is what clears the soft-delete state of API Management and the Foundry account, which
    # otherwise keep both names reserved and the model quota allocated.
    Invoke-BestEffort 'Deleting the Azure resources' {
        Invoke-Native azd @('down', '--cwd', $repoRoot, '--environment', $EnvironmentName, '--force', '--purge')
    }
}
else {
    # No local environment to drive azd with. The template is subscription scoped and creates the
    # group, so deleting the group is equivalent for everything inside it.
    "  no local azd environment '$EnvironmentName'; deleting the resource group directly"
    Invoke-BestEffort "Deleting resource group '$ResourceGroupName'" {
        Invoke-Native az @('group', 'delete', '--name', $ResourceGroupName, '--yes')
    }
}

$resourcesDeleted = $teardownFailures.Count -eq $failuresBeforeDelete

Write-Step 'Purging soft-deleted resources'

# Only ever purges resources belonging to this environment. A subscription can hold soft-deleted
# accounts from unrelated work, and those are somebody else's to restore.
$purged = 0

$deletedAccounts = @()
Invoke-BestEffort 'Listing the soft-deleted Foundry accounts' {
    $script:deletedAccounts = @(az cognitiveservices account list-deleted --output json | ConvertFrom-Json)
}

foreach ($deleted in $deletedAccounts) {
    $matchesEnvironment = if ($foundryAccountName) { $deleted.name -eq $foundryAccountName } else { $deleted.name -like "aif-$EnvironmentName-*" }
    if (-not $matchesEnvironment) { continue }

    # The purge is addressed by the group and region the account was deleted from, both of which
    # survive only in the deleted account's own id.
    $segments = $deleted.id -split '/'
    $location = $segments[[array]::IndexOf($segments, 'locations') + 1]
    $group = $segments[[array]::IndexOf($segments, 'resourceGroups') + 1]

    Invoke-BestEffort "Purging the soft-deleted Foundry account '$($deleted.name)'" -Hint 'Azure purges it on its own after about 48 hours. Until then it goes on holding its model-deployment quota, and redeploying the same name into the same region will fail.' -Action {
        Invoke-Native az @('cognitiveservices', 'account', 'purge', '--name', $deleted.name, '--location', $location, '--resource-group', $group)
        "  purged Foundry account $($deleted.name)"
    }
    $purged++
}

$deletedApim = @()
Invoke-BestEffort 'Listing the soft-deleted API Management instances' {
    $script:deletedApim = @(az apim deletedservice list --output json 2>$null | ConvertFrom-Json)
}

foreach ($deleted in $deletedApim) {
    $matchesEnvironment = if ($apimServiceName) { $deleted.name -eq $apimServiceName } else { $deleted.name -like "apim-$EnvironmentName-*" }
    if (-not $matchesEnvironment) { continue }

    Invoke-BestEffort "Purging the soft-deleted API Management instance '$($deleted.name)'" {
        Invoke-Native az @('apim', 'deletedservice', 'purge', '--service-name', $deleted.name, '--location', $deleted.location)
        "  purged API Management $($deleted.name)"
    }
    $purged++
}

if ($purged -eq 0) { "  nothing belonging to '$EnvironmentName' was in a soft-deleted state" }

if (-not $KeepAppRegistration) {
    Write-Step 'Deleting the Entra app registrations'

    foreach ($appRegistrationName in $appRegistrations.Keys) {
        Invoke-BestEffort "Deleting the app registration '$appRegistrationName'" {
            # The cached appId can name an application an earlier run already deleted, so confirm
            # it is still in the directory rather than reporting its absence as a failure.
            $appId = $appRegistrations[$appRegistrationName]
            if ($appId) { $appId = az ad app list --filter "appId eq '$appId'" --query '[0].appId' -o tsv }
            if (-not $appId) { $appId = az ad app list --display-name $appRegistrationName --query '[0].appId' -o tsv }

            if ([string]::IsNullOrWhiteSpace($appId)) {
                "  no app registration named '$appRegistrationName'; nothing to delete"
            }
            else {
                # Deleting the application usually takes its service principal with it, but not
                # reliably enough to leave a stale principal holding role assignments in the tenant.
                $spObjectId = az ad sp list --filter "appId eq '$appId'" --query '[0].id' -o tsv
                if (-not [string]::IsNullOrWhiteSpace($spObjectId)) {
                    Invoke-Native az @('ad', 'sp', 'delete', '--id', $appId)
                    "  deleted service principal $spObjectId"
                }

                Invoke-Native az @('ad', 'app', 'delete', '--id', $appId)
                "  deleted app registration $appRegistrationName ($appId)"
            }
        }
    }
}

Write-Step 'Deleting Entra agent identities'

# Publishing an agent creates an Entra blueprint application, its principal, and a per-agent
# identity principal. None of them are ARM resources, so deleting the resource group leaves all
# three behind, and another set appears for every agent the next time an environment is deployed.
# Matching on the account and project names keeps this to identities this project created.
$agentIdentityPrefix = if ($foundryAccountName -and $foundryProjectName) { "$foundryAccountName-$foundryProjectName-" } else { "aif-$EnvironmentName-" }
$agentIdentityFilter = { $_.displayName -like "$agentIdentityPrefix*AgentIdentity*" }

# --all enumerates every principal in the tenant, so ask the directory to do the prefix match and
# return only the two fields the deletions need. The quotes are part of the values because az is a
# batch file: PowerShell leaves space-free arguments unquoted and cmd then reads the parentheses
# and brackets as syntax of its own.
$agentIdentityStartsWith = "`"startswith(displayName,'$agentIdentityPrefix')`""
$agentIdentityQuery = '"[].{id:id,displayName:displayName}"'

# Blueprints first: an application cannot be deleted through its own principal, and deleting it
# takes that principal with it, so clearing them ahead of the principal sweep leaves the sweep with
# only the per-agent identities, which have no application to be removed by.
$agentApps = @()
Invoke-BestEffort 'Deleting the Entra agent identity blueprints' {
    $script:agentApps = @(az ad app list --filter $agentIdentityStartsWith --query $agentIdentityQuery --output json | ConvertFrom-Json | Where-Object $agentIdentityFilter)
    foreach ($app in $agentApps) {
        if ([string]::IsNullOrWhiteSpace($app.id)) { continue }
        az ad app delete --id $app.id 2>$null
        if ($LASTEXITCODE -eq 0) { "  deleted blueprint $($app.displayName)" }
        else { Write-Warning "Could not delete app registration '$($app.displayName)'." }
    }
}

$agentPrincipals = @()
Invoke-BestEffort 'Deleting the Entra agent identity principals' {
    $script:agentPrincipals = @(az ad sp list --filter $agentIdentityStartsWith --query $agentIdentityQuery --output json | ConvertFrom-Json | Where-Object $agentIdentityFilter)
    foreach ($principal in $agentPrincipals) {
        if ([string]::IsNullOrWhiteSpace($principal.id)) { continue }
        az ad sp delete --id $principal.id 2>$null
        if ($LASTEXITCODE -eq 0) { "  deleted principal $($principal.displayName)" }
        else { Write-Warning "Could not delete service principal '$($principal.displayName)'." }
    }
}

if ($agentPrincipals.Count -eq 0 -and $agentApps.Count -eq 0) { "  no agent identities matching '$agentIdentityPrefix*'" }

if (-not $KeepEnvironmentFiles -and -not $resourcesDeleted) {
    Write-Step 'Keeping the local azd environments'

    # They are what a rerun needs to drive azd, and the generated resource names live nowhere else.
    "  the Azure resources were not fully deleted; rerun this script to finish the teardown"
}
elseif (-not $KeepEnvironmentFiles) {
    Write-Step 'Removing local azd environments'

    foreach ($env in @(
            @{ Name = $EnvironmentName; Cwd = $repoRoot },
            @{ Name = $OrchestratorEnvironmentName; Cwd = $orchestratorRoot })) {

        if (Test-AzdEnv -Name $env.Name -Cwd $env.Cwd) {
            Invoke-BestEffort "Removing the local azd environment '$($env.Name)'" {
                Invoke-Native azd @('env', 'remove', $env.Name, '--cwd', $env.Cwd, '--force')
                "  removed '$($env.Name)'"
            }
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

if ($teardownFailures.Count -gt 0) {
    ''
    Write-Warning "$($teardownFailures.Count) step(s) did not complete:"
    foreach ($failure in $teardownFailures) { Write-Warning "  - $failure" }
    Write-Warning 'Rerun this script to retry them. Anything already deleted is skipped.'
}

''
'Redeploy with:'
"  ./scripts/New-Deployment.ps1 -TenantId <tenant-id> -SubscriptionId <subscription-id> ``"
"      -NwsUserAgent '<contact string>' -ApimPublisherEmail <address>"
