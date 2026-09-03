<#
.SYNOPSIS
Checks RBAC and quota before azd provisions the two model deployments.
#>

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

function Write-WarningMessage([string]$Message) {
    Write-Host "Preflight: $Message" -ForegroundColor Yellow
}

$claudeModel = if ($env:CLAUDE_MODEL_NAME) { $env:CLAUDE_MODEL_NAME } else { "claude-opus-5" }
$claudeCapacity = if ($env:CLAUDE_MODEL_CAPACITY) { [int]$env:CLAUDE_MODEL_CAPACITY } else { 25 }
$aoaiModel = if ($env:AOAI_MODEL_NAME) { $env:AOAI_MODEL_NAME } else { "gpt-5.6-sol" }
$aoaiCapacity = if ($env:AOAI_MODEL_CAPACITY) { [int]$env:AOAI_MODEL_CAPACITY } else { 10 }

if (-not $env:CLAUDE_ORGANIZATION_NAME) {
    Write-WarningMessage "CLAUDE_ORGANIZATION_NAME is not set. azd will prompt for it. Set it first for unattended runs: azd env set CLAUDE_ORGANIZATION_NAME 'Your organization'"
}

if (-not $env:AZURE_LOCATION) {
    Write-WarningMessage "AZURE_LOCATION is not set. azd will prompt for a supported region; proactive quota checks are skipped."
    exit 0
}

$az = Get-Command az -ErrorAction SilentlyContinue
if (-not $az) {
    Write-WarningMessage "Azure CLI is not installed. Skipping proactive Marketplace and quota checks; ARM validation still runs during azd provision."
    exit 0
}

$subscriptionId = az account show --query id -o tsv 2>$null
if (-not $subscriptionId) {
    Write-WarningMessage "Azure CLI is not signed in. Skipping proactive Marketplace and quota checks."
    exit 0
}

Write-Host "Preflight: subscription $subscriptionId, location $env:AZURE_LOCATION"
Write-Host "Preflight: Claude $claudeModel@$claudeCapacity; OpenAI $aoaiModel@$aoaiCapacity"
Write-Host "Preflight: Claude Hosted on Azure requires a paid Marketplace-eligible subscription. modelProviderData will auto-accept the offer during deployment."

$permissionsUri = "https://management.azure.com/subscriptions/$subscriptionId/providers/Microsoft.Authorization/permissions?api-version=2015-07-01"
$roleAssignmentGrants = az rest --method get --url $permissionsUri --query "length(value[?contains(actions, '*') || contains(actions, 'Microsoft.Authorization/roleAssignments/write')])" -o tsv 2>$null
if ($roleAssignmentGrants -eq "0" -and $env:HOSTING_MODE -ne "local") {
    Write-WarningMessage @"
The signed-in principal does not appear to have Microsoft.Authorization/roleAssignments/write, which
Azure hosting requires. Either request the Role Based Access Control Administrator role, or provision
models only and run the app locally:
  azd env set HOSTING_MODE local
  azd env set ASSIGN_INFERENCE_ROLE_TO_DEPLOYER false
"@
}

foreach ($model in @(
    [pscustomobject]@{ Name = $claudeModel; Capacity = $claudeCapacity },
    [pscustomobject]@{ Name = $aoaiModel; Capacity = $aoaiCapacity }
)) {
    $sku = "AIServices.GlobalStandard.$($model.Name)"
    $limit = az cognitiveservices usage list --location $env:AZURE_LOCATION --query "[?name.value=='$sku'].limit | [0]" -o tsv 2>$null
    $current = az cognitiveservices usage list --location $env:AZURE_LOCATION --query "[?name.value=='$sku'].currentValue | [0]" -o tsv 2>$null

    if (-not $limit) {
        Write-WarningMessage "No quota row was returned for $sku. Verify model availability in the Foundry portal before a live demo."
        continue
    }

    $available = [int][double]$limit - [int][double]($current ?? 0)
    if ($available -lt $model.Capacity) {
        Write-WarningMessage "$($model.Name) requests $($model.Capacity) but only $available quota units appear available. ARM preflight will confirm and offer model/region adjustments."
    }
    else {
        Write-Host "Preflight: $($model.Name) has $available quota units available."
    }
}
