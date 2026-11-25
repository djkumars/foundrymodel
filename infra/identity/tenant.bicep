targetScope = 'resourceGroup'

@description('Globally unique directory resource name. Verify the current provider requirements before deploying.')
@minLength(1)
param directoryResourceName string
@description('Display name for the new customer tenant.')
@minLength(1)
param displayName string
@description('Country code for data residency, for example US. This choice cannot be changed.')
@minLength(2)
@maxLength(2)
param countryCode string
@description('Tenant data residency location, not the Azure resource-group region.')
@allowed(['United States', 'Europe', 'Asia Pacific', 'Australia'])
param tenantDataLocation string
@description('Tenant SKU supported by the provider for the target subscription; confirm before deploying.')
param tenantSkuName string

resource tenant 'Microsoft.AzureActiveDirectory/ciamDirectories@2023-05-17-preview' = {
  name: directoryResourceName
  location: tenantDataLocation
  sku: {
    name: tenantSkuName
    tier: 'A0'
  }
  properties: {
    createTenantProperties: {
      displayName: displayName
      countryCode: countryCode
    }
  }
}

output tenantResourceId string = tenant.id
output tenantId string = tenant.properties.tenantId
