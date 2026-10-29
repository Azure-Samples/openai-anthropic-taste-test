[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $TemplatePath
)

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath $TemplatePath -Raw | ConvertFrom-Json

function Get-Resources($value) {
    $entries = if ($value.resources -is [array]) {
        $value.resources
    } else {
        $value.resources.PSObject.Properties.Value
    }
    foreach ($resource in $entries) {
        $resource
        if ($null -ne $resource.properties.template) {
            Get-Resources $resource.properties.template
        }
    }
}

$resources = @(Get-Resources $template)

function Get-SingleResource($type) {
    $matches = @($resources | Where-Object { ($_.type -replace '@v1\.0$', '') -eq $type })
    if ($matches.Count -ne 1) { throw "Expected one $type resource, found $($matches.Count)." }
    $matches[0]
}

function Assert-Equal($actual, $expected, $description) {
    if ($actual -cne $expected) { throw "${description}: expected '$expected', got '$actual'." }
}

$app = Get-SingleResource 'Microsoft.App/containerApps'
$auth = Get-SingleResource 'Microsoft.App/containerApps/authConfigs'
$hosting = @($resources | Where-Object {
    $_.type -eq 'Microsoft.Resources/deployments' -and
    $null -ne $_.properties.template.variables.federationSettingName
})
Assert-Equal $hosting.Count 1 'Hosting module count'
Assert-Equal $hosting[0].condition "[parameters('deployApplicationHost')]" 'Local mode creates no host or Entra application'
Assert-Equal $hosting[0].properties.template.variables.federationSettingName 'override-use-mi-fic-assertion-client-id' 'Federation setting name'
$provider = $auth.properties.identityProviders.azureActiveDirectory
Assert-Equal $auth.properties.platform.enabled $true 'Authentication enabled'
Assert-Equal $auth.properties.globalValidation.unauthenticatedClientAction 'RedirectToLoginPage' 'Anonymous requests'
Assert-Equal $auth.properties.globalValidation.redirectToProvider 'azureactivedirectory' 'Sign-in provider'
Assert-Equal (@($auth.properties.globalValidation.excludedPaths).Count) 1 'Anonymous path count'
Assert-Equal $auth.properties.globalValidation.excludedPaths[0] '/health' 'Anonymous health probe'
Assert-Equal $provider.enabled $true 'Entra provider'
Assert-Equal $provider.registration.clientSecretSettingName "[variables('federationSettingName')]" 'Federated client assertion'
Assert-Equal (@($provider.validation.allowedAudiences).Count) 1 'Token audience count'
Assert-Equal $provider.validation.allowedAudiences[0] $provider.registration.clientId 'Token audience'
Assert-Equal $auth.properties.login.tokenStore.enabled $false 'No delegated model tokens'

$environment = $app.properties.template.containers[0].env
$identity = @($environment | Where-Object name -eq 'AZURE_CLIENT_ID')
$credential = @($environment | Where-Object name -eq 'TasteTest__Credential__CredentialSource')
Assert-Equal $identity.Count 1 'Managed identity client ID'
Assert-Equal $credential.Count 1 'Hosted credential selector'
Assert-Equal $credential[0].value 'ManagedIdentityCredential' 'No developer credential fallback in Azure'
$configuredIdentity = @($environment | Where-Object name -eq 'TasteTest__Credential__ClientId')
Assert-Equal $configuredIdentity.Count 1 'Configured managed identity'
Assert-Equal $configuredIdentity[0].value $identity[0].value 'Client configuration uses the attached identity'
$gptEndpoint = @($environment | Where-Object name -eq 'TasteTest__Gpt__Options__Endpoint')
$claudeEndpoint = @($environment | Where-Object name -eq 'TasteTest__Claude__Options__Endpoint')
Assert-Equal $gptEndpoint.Count 1 'Configured OpenAI endpoint'
Assert-Equal $claudeEndpoint.Count 1 'Configured Anthropic endpoint'
if ($gptEndpoint[0].value -notlike '*/openai/v1/*' -or $claudeEndpoint[0].value -notlike '*/anthropic/*') {
    throw 'The provider base URLs must be configured explicitly.'
}
$federation = @($app.properties.configuration.secrets | Where-Object name -eq $provider.registration.clientSecretSettingName)
Assert-Equal $federation.Count 1 'Federation setting count'
Assert-Equal $federation[0].value $identity[0].value 'Federation uses the attached managed identity'

$registration = Get-SingleResource 'Microsoft.Graph/applications'
Assert-Equal $registration.properties.signInAudience 'AzureADMyOrg' 'Single-tenant sign-in'
Assert-Equal (@($registration.properties.web.redirectUris).Count) 1 'Callback count'
if ($registration.properties.web.redirectUris[0] -notlike '*/.auth/login/aad/callback*') {
    throw 'Entra callback must use the Container Apps built-in auth endpoint.'
}
Assert-Equal $registration.properties.web.implicitGrantSettings.enableAccessTokenIssuance $false 'No implicit access tokens'
if ($null -ne $registration.properties.passwordCredentials -or $null -ne $registration.properties.keyCredentials) {
    throw 'The Entra application must not create client secrets or certificates.'
}

$federatedCredential = Get-SingleResource 'Microsoft.Graph/applications/federatedIdentityCredentials'
Assert-Equal (@($federatedCredential.properties.audiences).Count) 1 'Federation audience count'
Assert-Equal $federatedCredential.properties.audiences[0] 'api://AzureADTokenExchange' 'Federation audience'
Assert-Equal $federatedCredential.properties.subject "[parameters('identityPrincipalId')]" 'Federation subject'
$webModule = @($resources | Where-Object {
    $_.type -eq 'Microsoft.Resources/deployments' -and
    $null -ne $_.properties.template.parameters.identityPrincipalId
})
Assert-Equal $webModule.Count 1 'Entra registration module count'
if ($webModule[0].properties.template.variables.issuer -notlike '*tenant().tenantId*') {
    throw 'Entra issuer must be bound to the deployment tenant, not a multitenant authority.'
}
Assert-Equal $federatedCredential.properties.issuer "[variables('issuer')]" 'Federation issuer'
Assert-Equal $webModule[0].properties.template.outputs.openIdIssuer.value "[variables('issuer')]" 'Login issuer'

Write-Output 'PASS: compiled hosting requires single-tenant Entra authentication and secretless managed-identity federation.'
