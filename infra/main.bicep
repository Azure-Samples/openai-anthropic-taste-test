targetScope = 'subscription'

@description('The azd environment name used for resource naming and tags.')
@minLength(1)
@maxLength(40)
param environmentName string

@description('Azure region where both default models are generally available.')
@allowed([
  'eastus2'
  'swedencentral'
])
@metadata({
  azd: {
    type: 'location'
  }
})
param location string

@description('Object ID of the deploying user or service principal. azd supplies this automatically.')
param principalId string = ''

@description('Anthropic model ID.')
param claudeModelName string = 'claude-sonnet-5-5'

@description('Anthropic model version. Version 2 is Hosted on Azure.')
param claudeModelVersion string = '2'

@description('Anthropic deployment capacity in thousands of tokens per minute.')
@minValue(1)
param claudeModelCapacity int = 10

@description('OpenAI model ID.')
param aoaiModelName string = 'gpt-6.1-sol'

@description('OpenAI model version.')
param aoaiModelVersion string = '2026-09-29'

@description('OpenAI deployment capacity in thousands of tokens per minute.')
@minValue(1)
param aoaiModelCapacity int = 10

@description('Organization name submitted when the Anthropic Azure Marketplace offer is accepted.')
@minLength(1)
param claudeOrganizationName string

@description('Two-letter ISO country code submitted for the Anthropic offer.')
@minLength(2)
@maxLength(2)
param claudeCountryCode string = 'US'

@description('Industry submitted for the Anthropic offer.')
@allowed([
  'technology'
  'finance'
  'healthcare'
  'education'
  'retail'
  'manufacturing'
  'government'
  'media'
  'other'
])
param claudeIndustry string = 'technology'

@description('Maximum output tokens per lane, including reasoning tokens as well as visible text.')
@minValue(128)
@maxValue(16384)
param maxOutputTokens int = 4096

@description('Export model/tool telemetry to application console logs. Prompts, answers and tool arguments are excluded.')
param telemetryConsole bool = false

@description('Optional OTLP HTTP/protobuf collector base URL. Leave empty to disable OTLP export.')
param otlpEndpoint string = ''

@description('Minimum Container Apps replicas. Use 1 for a warm demo or 0 to minimize idle cost.')
@minValue(0)
@maxValue(1)
param containerMinReplicas int = 1

@description('''
Where the application runs. "containerapp" provisions Azure hosting and needs permission to create
role assignments. "local" provisions only the Foundry account and both model deployments, creates no
role assignments, and is meant for running the app on your own machine under your own Entra identity.
''')
@allowed([
  'containerapp'
  'local'
])
param hostingMode string = 'containerapp'

@description('''
Grants the deploying principal the Cognitive Services User role on the Foundry account. Set this to
false when you lack Microsoft.Authorization/roleAssignments/write and already have Foundry data
access through another role such as Azure AI Developer.
''')
param assignInferenceRoleToDeployer bool = true

var deployApplicationHost = hostingMode == 'containerapp'

var suffix = take(uniqueString(subscription().id, environmentName, location), 8)
var resourceGroupName = 'rg-${environmentName}'
var tags = {
  'azd-env-name': environmentName
  application: 'openai-anthropic-taste-test'
}

resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

module application 'resources.bicep' = {
  name: 'taste-test-${suffix}'
  scope: resourceGroup
  params: {
    location: location
    tags: tags
    suffix: suffix
    principalId: principalId
    claudeModelName: claudeModelName
    claudeModelVersion: claudeModelVersion
    claudeModelCapacity: claudeModelCapacity
    aoaiModelName: aoaiModelName
    aoaiModelVersion: aoaiModelVersion
    aoaiModelCapacity: aoaiModelCapacity
    claudeOrganizationName: claudeOrganizationName
    claudeCountryCode: claudeCountryCode
    claudeIndustry: claudeIndustry
    maxOutputTokens: maxOutputTokens
    telemetryConsole: telemetryConsole
    otlpEndpoint: otlpEndpoint
    containerMinReplicas: containerMinReplicas
    deployApplicationHost: deployApplicationHost
    assignInferenceRoleToDeployer: assignInferenceRoleToDeployer
  }
}

output AZURE_LOCATION string = location
output AZURE_RESOURCE_GROUP string = resourceGroup.name
output HOSTING_MODE string = hostingMode
output AZURE_FOUNDRY_RESOURCE_NAME string = application.outputs.foundryResourceName
output AZURE_FOUNDRY_ENDPOINT string = application.outputs.foundryEndpoint
output AZURE_AI_PROJECT_ENDPOINT string = application.outputs.foundryProjectEndpoint
output AOAI_DEPLOYMENT_NAME string = application.outputs.aoaiDeploymentName
output CLAUDE_DEPLOYMENT_NAME string = application.outputs.claudeDeploymentName
output GPT_ENDPOINT string = application.outputs.gptEndpoint
output CLAUDE_ENDPOINT string = application.outputs.claudeEndpoint
output AZURE_CONTAINER_ENVIRONMENT_NAME string = application.outputs.containerEnvironmentName
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = application.outputs.containerRegistryEndpoint
output AZURE_CONTAINER_REGISTRY_NAME string = application.outputs.containerRegistryName
output SERVICE_WEB_IDENTITY_PRINCIPAL_ID string = application.outputs.identityPrincipalId
output SERVICE_WEB_IMAGE_NAME string = application.outputs.imageName
output SERVICE_WEB_NAME string = application.outputs.containerAppName
output SERVICE_WEB_URI string = application.outputs.containerAppUri
output WEB_AUTH_CLIENT_ID string = application.outputs.webAuthClientId
