<#
.SYNOPSIS
    Stands the whole system up in an empty subscription, from sign-in to a deployed orchestrator.

.DESCRIPTION
    The repository is two azd projects, and neither one can be provisioned from inside the other:
    the root uses the Bicep provider and owns every Azure resource, while orchestrator/ uses the
    microsoft.foundry provider and only deploys an agent onto what the root created. This script is
    the order those two have to run in, plus the values that have to exist before either will.

    What it does, in sequence:

      1. Signs azd and the Azure CLI in to the same tenant. Both are needed: the preprovision hook
         creates an Entra app registration and the postdeploy hook publishes the specialists, and
         both of those use `az`, not `azd`.
      2. Creates or selects the root azd environment and sets the values Bicep needs.
      3. Runs `azd up`, which provisions everything, deploys the function app, and publishes the
         four specialist agents through the postdeploy hook.
      4. Copies the Foundry endpoint and model name out of the root outputs into a second azd
         environment under orchestrator/, and deploys the hosted orchestrator against them.

    Rerunning is safe. An environment that already describes the same subscription and the same
    Foundry project is reused and its values reset from the arguments given; one that describes
    anything else is discarded and rebuilt, so a rerun against a new subscription cannot inherit
    resource ids from the last one.

.PARAMETER ConfigureOnly
    Stops after the environment is populated, before anything is provisioned. Useful for reviewing
    `azd env get-values` or running `azd provision --preview` by hand first.

.PARAMETER SkipOrchestrator
    Stops after `azd up`. The backend, the gateway, and the four specialists are deployed; the
    hosted orchestrator is not.

.EXAMPLE
    ./scripts/New-Deployment.ps1 -TenantId <tenant-id> -SubscriptionId <subscription-id> `
        -NwsUserAgent 'ERDC.Agents (you@example.com)' -ApimPublisherEmail you@example.com

.EXAMPLE
    # Basicv2 provisions in minutes instead of the 30 to 45 the Developer tier takes.
    ./scripts/New-Deployment.ps1 -TenantId <tenant-id> -SubscriptionId <subscription-id> `
        -NwsUserAgent 'ERDC.Agents (you@example.com)' -ApimPublisherEmail you@example.com `
        -ApimSku Basicv2
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$TenantId,
    [Parameter(Mandatory)][string]$SubscriptionId,

    # The National Weather Service requires a contact string on every request and blocks callers
    # that do not send one.
    [Parameter(Mandatory)][string]$NwsUserAgent,

    # Where API Management sends service notifications. Required by the ARM resource itself.
    [Parameter(Mandatory)][string]$ApimPublisherEmail,

    [string]$EnvironmentName = 'erdc-agents-dev',
    [string]$Location = 'eastus',
    [string]$ResourceGroupName,
    [string]$OrchestratorEnvironmentName = 'geo-orchestrator-dev',

    [ValidateSet('Developer', 'Basicv2', 'Standardv2', 'Premiumv2')]
    [string]$ApimSku = 'Developer',

    # Reuse an app registration that already exists instead of letting the preprovision hook create
    # one, for tenants where the Entra Application Developer role is not available.
    [string]$GeoApiAudience,

    [switch]$ConfigureOnly,
    [switch]$SkipOrchestrator
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
$orchestratorRoot = Join-Path $repoRoot 'orchestrator'
if (-not $ResourceGroupName) { $ResourceGroupName = "rg-$EnvironmentName" }

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

function Set-AzdValue {
    param([string]$Name, [string]$Value, [string]$Cwd)
    azd env set $Name $Value --cwd $Cwd | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "azd env set $Name failed with exit code $LASTEXITCODE." }
    "  {0,-32} {1}" -f $Name, $Value
}

function Get-AzdEnvValues {
    param([string]$Name, [string]$Cwd)
    $raw = azd env get-values --environment $Name --cwd $Cwd --output json 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $raw) { return @{} }

    $values = @{}
    foreach ($property in ($raw | ConvertFrom-Json).PSObject.Properties) { $values[$property.Name] = $property.Value }
    $values
}

function Initialize-AzdEnv {
    param(
        [string]$Name,
        [string]$Cwd,

        # An existing environment is only reused when its value for this key already matches. Anything
        # else is a leftover pointing at resources this deployment did not create, and is removed.
        [string]$IdentityKey,
        [string]$IdentityValue
    )

    $existing = azd env list --cwd $Cwd --output json 2>$null | ConvertFrom-Json
    if ($existing | Where-Object { $_.Name -eq $Name }) {
        $current = if ($IdentityKey) { (Get-AzdEnvValues -Name $Name -Cwd $Cwd)[$IdentityKey] } else { $IdentityValue }

        if (-not $IdentityKey -or $current -eq $IdentityValue) {
            Invoke-Native azd @('env', 'select', $Name, '--cwd', $Cwd)
            "  reusing existing azd environment '$Name'"
            return
        }

        # Discarded rather than repointed: azd writes far more than the two values set below into a
        # Foundry environment when it deploys, and every one of those describes the old target.
        Invoke-Native azd @('env', 'remove', '--environment', $Name, '--cwd', $Cwd, '--force')
        "  discarded azd environment '$Name'; its $IdentityKey was $(if ($current) { $current } else { 'unset' })"
    }

    Invoke-Native azd @('env', 'new', $Name, '--cwd', $Cwd,
        '--subscription', $SubscriptionId, '--location', $Location)
    "  created azd environment '$Name'"
}

foreach ($tool in 'azd', 'az') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "'$tool' is not on PATH. The Azure Developer CLI and the Azure CLI are both required."
    }
}

Write-Step 'Signing in'

# azd provisions; az is what the two hooks call. Signing in to only one leaves a deployment that
# fails at the hook rather than at the start.
azd auth login --check-status --tenant-id $TenantId | Out-Null
if ($LASTEXITCODE -ne 0) { Invoke-Native azd @('auth', 'login', '--tenant-id', $TenantId) }

$account = az account show 2>$null | ConvertFrom-Json
if (-not $account -or $account.tenantId -ne $TenantId) {
    Invoke-Native az @('login', '--tenant', $TenantId)
}
Invoke-Native az @('account', 'set', '--subscription', $SubscriptionId)

Write-Step "Configuring azd environment '$EnvironmentName'"

Initialize-AzdEnv -Name $EnvironmentName -Cwd $repoRoot -IdentityKey AZURE_SUBSCRIPTION_ID -IdentityValue $SubscriptionId

# azd substitutes an empty string for an unset variable, and an empty string overrides a Bicep
# default rather than falling back to it. Anything main.parameters.json reads has to be set here
# even where main.bicep declares a default for it.
Set-AzdValue AZURE_LOCATION $Location $repoRoot
Set-AzdValue AZURE_TENANT_ID $TenantId $repoRoot
Set-AzdValue AZURE_RESOURCE_GROUP $ResourceGroupName $repoRoot
Set-AzdValue NWS_USER_AGENT $NwsUserAgent $repoRoot
Set-AzdValue APIM_PUBLISHER_EMAIL $ApimPublisherEmail $repoRoot
Set-AzdValue APIM_SKU $ApimSku $repoRoot
if ($GeoApiAudience) { Set-AzdValue GEO_API_AUDIENCE $GeoApiAudience $repoRoot }

if ($ConfigureOnly) {
    Write-Step 'Configured. Nothing provisioned because -ConfigureOnly was given.'
    "Review with: azd env get-values"
    "Continue with: azd up"
    return
}

if ($ApimSku -eq 'Developer') {
    Write-Warning 'API Management Developer takes 30 to 45 minutes to provision. Pass -ApimSku Basicv2 to trade cost for time.'
}

Write-Step 'Provisioning and deploying the backend (azd up)'

# Provisions everything, deploys the function app, and runs the postdeploy hook that publishes the
# four specialist agents against the gateway it just created.
Invoke-Native azd @('up', '--cwd', $repoRoot, '--no-prompt')

if ($SkipOrchestrator) {
    Write-Step 'Backend deployed. Orchestrator skipped because -SkipOrchestrator was given.'
    return
}

Write-Step "Configuring azd environment '$OrchestratorEnvironmentName' from the backend outputs"

$config = & (Join-Path $PSScriptRoot 'Get-AzdConfig.ps1') -Require FOUNDRY_PROJECT_ENDPOINT, ORCHESTRATOR_MODEL

# Deploying the orchestrator makes azd record the account, the project id, the model deployments,
# and the published agent version in this environment. An environment left over from a different
# Foundry project therefore describes resources this run did not create, and setting the endpoint
# alone would not correct the rest, so a mismatched one is discarded and rebuilt from the outputs.
Initialize-AzdEnv -Name $OrchestratorEnvironmentName -Cwd $orchestratorRoot `
    -IdentityKey FOUNDRY_PROJECT_ENDPOINT -IdentityValue $config['FOUNDRY_PROJECT_ENDPOINT']

Set-AzdValue FOUNDRY_PROJECT_ENDPOINT $config['FOUNDRY_PROJECT_ENDPOINT'] $orchestratorRoot
Set-AzdValue AZURE_AI_MODEL_DEPLOYMENT_NAME $config['ORCHESTRATOR_MODEL'] $orchestratorRoot

Write-Step 'Deploying the orchestrator'

# Deploy the named service only. Provisioning from here would give the Foundry account, project, and
# model deployments a second owner; the root project's Bicep already created all three.
Invoke-Native azd @('deploy', 'geo-orchestrator', '--cwd', $orchestratorRoot)

Write-Step 'Done'
"  Gateway         $($config['GEO_API_BASE_URL'])"
"  Foundry project $($config['FOUNDRY_PROJECT_ENDPOINT'])"
''
"Smoke test:"
"  ./orchestrator/ask.ps1 -Message 'Conditions at 51.5072, -0.1276?' -Deployed"
