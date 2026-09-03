@description('Azure region for all resources.')
param location string

@description('Resource tags.')
param tags object

@description('Unique resource-name suffix.')
param suffix string

@description('Name of the existing Foundry account the application calls.')
param foundryAccountName string

param aoaiDeploymentName string
param claudeDeploymentName string
param maxOutputTokens int

@description('Minimum replicas. Use 1 for a warm demo or 0 to minimize idle cost.')
@minValue(0)
@maxValue(1)
param containerMinReplicas int

var containerAppName = 'ca-taste-${suffix}'
var containerEnvironmentName = 'cae-taste-${suffix}'
var containerRegistryName = 'acrtaste${suffix}'
var managedIdentityName = 'id-taste-${suffix}'
var logAnalyticsName = 'log-taste-${suffix}'
var cognitiveServicesUserRoleId = 'a97b65f3-24c7-4388-baec-2e87135dc908'
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource foundry 'Microsoft.CognitiveServices/accounts@2025-10-01-preview' existing = {
  name: foundryAccountName
}

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    retentionInDays: 30
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
    }
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
  #disable-next-line BCP187
  sku: {
    name: 'PerGB2018'
  }
}

resource containerEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: containerEnvironmentName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: containerRegistryName
  location: location
  tags: tags
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
    dataEndpointEnabled: false
    networkRuleBypassOptions: 'AzureServices'
    publicNetworkAccess: 'Enabled'
  }
}

resource appIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: managedIdentityName
  location: location
  tags: tags
}

resource acrPullRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containerRegistry.id, appIdentity.id, acrPullRoleId)
  scope: containerRegistry
  properties: {
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      acrPullRoleId)
  }
}

resource appInferenceRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundry.id, appIdentity.id, cognitiveServicesUserRoleId)
  scope: foundry
  properties: {
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      cognitiveServicesUserRoleId)
  }
}

resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: containerAppName
  location: location
  tags: union(tags, {
    'azd-service-name': 'web'
  })
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${appIdentity.id}': {}
    }
  }
  properties: {
    environmentId: containerEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        allowInsecure: false
        external: true
        targetPort: 8080
        transport: 'auto'
      }
      registries: [
        {
          identity: appIdentity.id
          server: containerRegistry.properties.loginServer
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'web'
          // Placeholder until `azd deploy` pushes the application image.
          image: 'mcr.microsoft.com/dotnet/samples:aspnetapp'
          env: [
            {
              name: 'AZURE_CLIENT_ID'
              value: appIdentity.properties.clientId
            }
            {
              name: 'AZURE_FOUNDRY_ENDPOINT'
              value: 'https://${foundry.name}.services.ai.azure.com'
            }
            {
              name: 'AZURE_FOUNDRY_RESOURCE_NAME'
              value: foundry.name
            }
            {
              name: 'AOAI_DEPLOYMENT_NAME'
              value: aoaiDeploymentName
            }
            {
              name: 'CLAUDE_DEPLOYMENT_NAME'
              value: claudeDeploymentName
            }
            {
              name: 'TASTE_TEST_MAX_OUTPUT_TOKENS'
              value: string(maxOutputTokens)
            }
            {
              name: 'ASPNETCORE_URLS'
              value: 'http://+:8080'
            }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/health'
                port: 8080
                scheme: 'HTTP'
              }
              initialDelaySeconds: 20
              periodSeconds: 30
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health'
                port: 8080
                scheme: 'HTTP'
              }
              initialDelaySeconds: 5
              periodSeconds: 10
            }
          ]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
      // Blazor Server keeps circuit state in memory, so the demo stays on one replica.
      scale: {
        minReplicas: containerMinReplicas
        maxReplicas: 1
      }
    }
  }
  dependsOn: [
    acrPullRole
    appInferenceRole
  ]
}

output containerEnvironmentName string = containerEnvironment.name
output containerRegistryEndpoint string = containerRegistry.properties.loginServer
output containerRegistryName string = containerRegistry.name
output identityPrincipalId string = appIdentity.properties.principalId
output containerAppName string = containerApp.name
output containerAppUri string = 'https://${containerApp.properties.configuration.ingress.fqdn}'
