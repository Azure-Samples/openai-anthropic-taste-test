@description('Azure region for all resources.')
param location string

@description('Resource tags.')
param tags object

@description('Unique resource-name suffix.')
param suffix string

@description('Object ID of the deploying user or service principal.')
param principalId string = ''

param claudeModelName string
param claudeModelVersion string
param claudeModelCapacity int
param aoaiModelName string
param aoaiModelVersion string
param aoaiModelCapacity int
param claudeOrganizationName string
param claudeCountryCode string
param claudeIndustry string
param maxOutputTokens int
param containerMinReplicas int

@description('Provisions Container Apps hosting, its registry, identity, and role assignments.')
param deployApplicationHost bool = true

@description('Assigns Cognitive Services User to the deploying principal.')
param assignInferenceRoleToDeployer bool = true

var foundryResourceName = 'ai-taste-${suffix}'
var foundryProjectName = 'taste-test'
var cognitiveServicesUserRoleId = 'a97b65f3-24c7-4388-baec-2e87135dc908'

resource foundry 'Microsoft.CognitiveServices/accounts@2025-10-01-preview' = {
  name: foundryResourceName
  location: location
  tags: tags
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    allowProjectManagement: true
    customSubDomainName: foundryResourceName
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2025-10-01-preview' = {
  parent: foundry
  name: foundryProjectName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {}
}

resource aoaiDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-10-01-preview' = {
  parent: foundry
  name: aoaiModelName
  sku: {
    name: 'GlobalStandard'
    capacity: aoaiModelCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: aoaiModelName
      version: aoaiModelVersion
    }
    raiPolicyName: 'Microsoft.DefaultV2'
    versionUpgradeOption: 'NoAutoUpgrade'
  }
  dependsOn: [
    project
  ]
}

resource claudeDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-10-01-preview' = {
  parent: foundry
  name: claudeModelName
  sku: {
    name: 'GlobalStandard'
    capacity: claudeModelCapacity
  }
  properties: {
    model: {
      format: 'Anthropic'
      name: claudeModelName
      version: claudeModelVersion
    }
    #disable-next-line BCP037
    modelProviderData: {
      organizationName: claudeOrganizationName
      countryCode: claudeCountryCode
      industry: claudeIndustry
    }
    raiPolicyName: 'Microsoft.DefaultV2'
    versionUpgradeOption: 'NoAutoUpgrade'
  }
  dependsOn: [
    aoaiDeployment
    project
  ]
}


module hosting 'hosting.bicep' = if (deployApplicationHost) {
  name: 'taste-test-hosting'
  params: {
    location: location
    tags: tags
    suffix: suffix
    foundryAccountName: foundry.name
    aoaiDeploymentName: aoaiDeployment.name
    claudeDeploymentName: claudeDeployment.name
    maxOutputTokens: maxOutputTokens
    containerMinReplicas: containerMinReplicas
  }
}

resource deployerInferenceRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (assignInferenceRoleToDeployer && !empty(principalId)) {
  name: guid(foundry.id, principalId, cognitiveServicesUserRoleId)
  scope: foundry
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      cognitiveServicesUserRoleId)
  }
}

output foundryResourceName string = foundry.name
output foundryEndpoint string = 'https://${foundry.name}.services.ai.azure.com'
output foundryProjectEndpoint string = 'https://${foundry.name}.services.ai.azure.com/api/projects/${project.name}'
output aoaiDeploymentName string = aoaiDeployment.name
output claudeDeploymentName string = claudeDeployment.name
output containerEnvironmentName string = hosting.?outputs.containerEnvironmentName ?? ''
output containerRegistryEndpoint string = hosting.?outputs.containerRegistryEndpoint ?? ''
output containerRegistryName string = hosting.?outputs.containerRegistryName ?? ''
output identityPrincipalId string = hosting.?outputs.identityPrincipalId ?? ''
output imageName string = ''
output containerAppName string = hosting.?outputs.containerAppName ?? ''
output containerAppUri string = hosting.?outputs.containerAppUri ?? ''
