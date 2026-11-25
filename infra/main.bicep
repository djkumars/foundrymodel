targetScope = 'resourceGroup'

@description('Azure region; model and Content Safety availability and quota must be checked before deployment.')
param location string = 'eastus2'
@description('GUID of the Microsoft Entra External ID tenant.')
param externalTenantId string
@description('External ID tenant subdomain, without .ciamlogin.com.')
param externalTenantName string
@description('Client ID of the External ID API registration.')
param externalApiClientId string
@description('Public IPv4 CIDR permitted to deploy to the App Service SCM endpoint, for example 203.0.113.10/32.')
param deploymentIpCidr string
@description('Static website hostname after enabling website hosting, without https:// or a trailing slash. Empty skips edge routes.')
param webOriginHostName string = ''
@description('A supported GPT-4o model version in the selected region.')
param modelVersion string = '2024-11-20'
@description('A supported GPT-4o deployment SKU in the selected region.')
param modelSku string = 'Standard'
@minValue(1)
param modelCapacity int = 1

var suffix = uniqueString(resourceGroup().id)

resource profile 'Microsoft.Cdn/profiles@2024-02-01' = {
  name: 'afd-chat-${suffix}'
  location: 'global'
  sku: { name: 'Standard_AzureFrontDoor' }
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-chat-api-${suffix}'
  location: location
}

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-chat-${suffix}'
  location: location
  properties: {
    retentionInDays: 30
    sku: { name: 'PerGB2018' }
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-chat-${suffix}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    RetentionInDays: 30
  }
}

module network './modules/network.bicep' = {
  name: 'network'
  params: { location: location, suffix: suffix }
}

module services './modules/services.bicep' = {
  name: 'services'
  params: {
    location: location
    suffix: suffix
    subnetId: network.outputs.subnetId
    appPrincipalId: identity.properties.principalId
    modelVersion: modelVersion
    modelSku: modelSku
    modelCapacity: modelCapacity
  }
}

module app './modules/app.bicep' = {
  name: 'api'
  params: {
    location: location
    suffix: suffix
    subnetId: network.outputs.subnetId
    identityId: identity.id
    frontDoorId: profile.properties.frontDoorId
    vaultUri: services.outputs.vaultUri
    cosmosEndpoint: services.outputs.cosmosEndpoint
    foundryEndpoint: services.outputs.foundryEndpoint
    safetyEndpoint: services.outputs.safetyEndpoint
    externalTenantId: externalTenantId
    externalTenantName: externalTenantName
    externalApiClientId: externalApiClientId
    deploymentIpCidr: deploymentIpCidr
    appInsightsConnectionString: insights.properties.ConnectionString
  }
}

module edge './modules/edge.bicep' = if (!empty(webOriginHostName)) {
  name: 'edge'
  params: {
    suffix: suffix
    profileName: profile.name
    webOriginHostName: webOriginHostName
    apiOriginHostName: app.outputs.hostName
    workspaceId: workspace.id
  }
}

output storageAccountName string = services.outputs.storageAccountName
output apiAppName string = app.outputs.appName
output foundryProjectId string = services.outputs.foundryProjectId
output frontDoorHostName string = empty(webOriginHostName) ? '' : edge!.outputs.hostName
