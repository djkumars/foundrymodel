param location string
param suffix string
param subnetId string
param identityId string
param frontDoorId string
param vaultUri string
param cosmosEndpoint string
param foundryEndpoint string
param safetyEndpoint string
param externalTenantId string
param externalTenantName string
param externalApiClientId string
param deploymentIpCidr string
param appInsightsConnectionString string

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'plan-chat-${suffix}'
  location: location
  kind: 'linux'
  sku: { name: 'B1', tier: 'Basic', capacity: 1 }
  properties: { reserved: true }
}

resource app 'Microsoft.Web/sites@2023-12-01' = {
  name: 'api-chat-${suffix}'
  location: location
  kind: 'app,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identityId}': {} }
  }
  properties: {
    serverFarmId: plan.id
    virtualNetworkSubnetId: subnetId
    vnetRouteAllEnabled: true
    keyVaultReferenceIdentity: identityId
    httpsOnly: true
    publicNetworkAccess: 'Enabled'
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      healthCheckPath: '/api/health'
      ipSecurityRestrictionsDefaultAction: 'Deny'
      ipSecurityRestrictions: [
        {
          name: 'OnlyThisFrontDoor'
          action: 'Allow'
          priority: 100
          tag: 'ServiceTag'
          ipAddress: 'AzureFrontDoor.Backend'
          headers: { 'x-azure-fdid': [frontDoorId] }
        }
      ]
      scmIpSecurityRestrictionsUseMain: false
      scmIpSecurityRestrictionsDefaultAction: 'Deny'
      scmIpSecurityRestrictions: [
        {
          name: 'DeploymentAddress'
          action: 'Allow'
          priority: 100
          ipAddress: deploymentIpCidr
        }
      ]
      appSettings: [
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
        { name: 'WEBSITE_RUN_FROM_PACKAGE', value: '1' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
        { name: 'Foundry__Endpoint', value: foundryEndpoint }
        { name: 'Foundry__Model', value: 'gpt-4o' }
        { name: 'Foundry__ApiKey', value: '@Microsoft.KeyVault(SecretUri=${vaultUri}secrets/foundry-key/)' }
        { name: 'PromptShield__Endpoint', value: safetyEndpoint }
        { name: 'PromptShield__ApiKey', value: '@Microsoft.KeyVault(SecretUri=${vaultUri}secrets/prompt-shield-key/)' }
        { name: 'CosmosDb__Endpoint', value: cosmosEndpoint }
        { name: 'CosmosDb__Key', value: '@Microsoft.KeyVault(SecretUri=${vaultUri}secrets/cosmos-key/)' }
        { name: 'CosmosDb__DatabaseName', value: 'chatbot' }
        { name: 'CosmosDb__UserContainerName', value: 'user-conversations' }
        { name: 'ExternalId__TenantId', value: externalTenantId }
        { name: 'ExternalId__Authority', value: 'https://${externalTenantName}.ciamlogin.com/${externalTenantId}/v2.0' }
        { name: 'ExternalId__Audience', value: externalApiClientId }
      ]
    }
  }
}

resource ftpBasicAuth 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2023-12-01' = {
  parent: app
  name: 'ftp'
  properties: { allow: false }
}

resource scmBasicAuth 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2023-12-01' = {
  parent: app
  name: 'scm'
  properties: { allow: false }
}

output appName string = app.name
output hostName string = app.properties.defaultHostName
