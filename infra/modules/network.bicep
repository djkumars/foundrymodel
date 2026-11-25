param location string
param suffix string

resource subnetNsg 'Microsoft.Network/networkSecurityGroups@2024-05-01' = {
  name: 'nsg-chat-app-${suffix}'
  location: location
}

resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: 'vnet-chat-${suffix}'
  location: location
  properties: {
    addressSpace: { addressPrefixes: ['10.42.0.0/16'] }
    subnets: [
      {
        name: 'app-integration'
        properties: {
          addressPrefix: '10.42.0.0/24'
          networkSecurityGroup: { id: subnetNsg.id }
          serviceEndpoints: [
            { service: 'Microsoft.AzureCosmosDB' }
            { service: 'Microsoft.CognitiveServices' }
            { service: 'Microsoft.KeyVault' }
          ]
          delegations: [
            {
              name: 'app-service'
              properties: { serviceName: 'Microsoft.Web/serverFarms' }
            }
          ]
        }
      }
    ]
  }
}

output subnetId string = '${vnet.id}/subnets/app-integration'
