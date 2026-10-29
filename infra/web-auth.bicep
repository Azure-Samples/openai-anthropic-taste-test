extension microsoftGraphV1

@description('Stable, tenant-wide unique name for this environment.')
param applicationName string

@description('HTTPS URL of the Container App.')
param applicationUrl string

@description('Object ID of the Container App user-assigned managed identity.')
param identityPrincipalId string

var issuer = '${environment().authentication.loginEndpoint}${tenant().tenantId}/v2.0'
var graphAppId = '00000003-0000-0000-c000-000000000000'

resource graph 'Microsoft.Graph/servicePrincipals@v1.0' existing = {
  appId: graphAppId
}

resource application 'Microsoft.Graph/applications@v1.0' = {
  uniqueName: applicationName
  displayName: 'OpenAI + Anthropic Taste Test'
  signInAudience: 'AzureADMyOrg'
  web: {
    redirectUris: [
      '${applicationUrl}/.auth/login/aad/callback'
    ]
    implicitGrantSettings: {
      // EasyAuth uses a hybrid sign-in flow; implicit access tokens stay disabled.
      enableIdTokenIssuance: true
      enableAccessTokenIssuance: false
    }
  }
  requiredResourceAccess: [
    {
      resourceAppId: graphAppId
      resourceAccess: [
        for scope in ['openid', 'profile']: {
          id: filter(graph.oauth2PermissionScopes, permission => permission.value == scope)[0].id
          type: 'Scope'
        }
      ]
    }
  ]

  resource federation 'federatedIdentityCredentials@v1.0' = {
    name: '${application.uniqueName}/container-app'
    audiences: [
      'api://AzureADTokenExchange'
    ]
    issuer: issuer
    subject: identityPrincipalId
  }
}

resource servicePrincipal 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: application.appId
}

output clientId string = application.appId
output openIdIssuer string = issuer
