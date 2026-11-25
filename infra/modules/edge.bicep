param suffix string
param profileName string
param webOriginHostName string
param apiOriginHostName string
param workspaceId string

resource profile 'Microsoft.Cdn/profiles@2024-02-01' existing = { name: profileName }

resource endpoint 'Microsoft.Cdn/profiles/afdEndpoints@2024-02-01' = {
  parent: profile
  name: 'chat-${suffix}'
  location: 'global'
  properties: { enabledState: 'Enabled' }
}

resource webGroup 'Microsoft.Cdn/profiles/originGroups@2024-02-01' = {
  parent: profile
  name: 'web'
  properties: {
    loadBalancingSettings: { sampleSize: 4, successfulSamplesRequired: 3 }
    healthProbeSettings: {
      probePath: '/'
      probeRequestType: 'HEAD'
      probeProtocol: 'Https'
      probeIntervalInSeconds: 100
    }
  }
}

resource webOrigin 'Microsoft.Cdn/profiles/originGroups/origins@2024-02-01' = {
  parent: webGroup
  name: 'storage'
  properties: {
    hostName: webOriginHostName
    originHostHeader: webOriginHostName
    httpsPort: 443
    httpPort: 80
    enabledState: 'Enabled'
    enforceCertificateNameCheck: true
    priority: 1
    weight: 1000
  }
}

resource apiGroup 'Microsoft.Cdn/profiles/originGroups@2024-02-01' = {
  parent: profile
  name: 'api'
  properties: {
    loadBalancingSettings: { sampleSize: 4, successfulSamplesRequired: 3 }
    healthProbeSettings: {
      probePath: '/api/health'
      probeRequestType: 'GET'
      probeProtocol: 'Https'
      probeIntervalInSeconds: 100
    }
  }
}

resource apiOrigin 'Microsoft.Cdn/profiles/originGroups/origins@2024-02-01' = {
  parent: apiGroup
  name: 'app-service'
  properties: {
    hostName: apiOriginHostName
    originHostHeader: apiOriginHostName
    httpsPort: 443
    httpPort: 80
    enabledState: 'Enabled'
    enforceCertificateNameCheck: true
    priority: 1
    weight: 1000
  }
}

resource rules 'Microsoft.Cdn/profiles/ruleSets@2024-02-01' = {
  parent: profile
  name: 'spa'
}

resource chatRewrite 'Microsoft.Cdn/profiles/ruleSets/rules@2024-02-01' = {
  parent: rules
  name: 'chatRoute'
  properties: {
    order: 1
    conditions: [
      {
        name: 'UrlPath'
        parameters: {
          typeName: 'DeliveryRuleUrlPathMatchConditionParameters'
          operator: 'Equal'
          matchValues: ['chat', 'chat/']
          transforms: []
          negateCondition: false
        }
      }
    ]
    actions: [
      {
        name: 'UrlRewrite'
        parameters: {
          typeName: 'DeliveryRuleUrlRewriteActionParameters'
          sourcePattern: '/chat'
          destination: '/index.html'
          preserveUnmatchedPath: false
        }
      }
    ]
  }
}

resource webRoute 'Microsoft.Cdn/profiles/afdEndpoints/routes@2024-02-01' = {
  parent: endpoint
  name: 'web'
  dependsOn: [webOrigin, chatRewrite]
  properties: {
    originGroup: { id: webGroup.id }
    ruleSets: [{ id: rules.id }]
    supportedProtocols: ['Http', 'Https']
    patternsToMatch: ['/*']
    forwardingProtocol: 'HttpsOnly'
    linkToDefaultDomain: 'Enabled'
    httpsRedirect: 'Enabled'
    cacheConfiguration: {
      queryStringCachingBehavior: 'IgnoreQueryString'
      compressionSettings: {
        isCompressionEnabled: true
        contentTypesToCompress: ['text/html', 'text/css', 'application/javascript', 'application/json']
      }
    }
  }
}

resource apiRoute 'Microsoft.Cdn/profiles/afdEndpoints/routes@2024-02-01' = {
  parent: endpoint
  name: 'api'
  dependsOn: [apiOrigin]
  properties: {
    originGroup: { id: apiGroup.id }
    supportedProtocols: ['Http', 'Https']
    patternsToMatch: ['/api/*']
    forwardingProtocol: 'HttpsOnly'
    linkToDefaultDomain: 'Enabled'
    httpsRedirect: 'Enabled'
  }
}

resource waf 'Microsoft.Network/frontDoorWebApplicationFirewallPolicies@2022-05-01' = {
  name: 'wafchat${suffix}'
  location: 'global'
  sku: { name: 'Standard_AzureFrontDoor' }
  properties: {
    policySettings: { enabledState: 'Enabled', mode: 'Prevention' }
    customRules: {
      rules: [
        {
          name: 'ApiRateLimit'
          enabledState: 'Enabled'
          priority: 1
          ruleType: 'RateLimitRule'
          rateLimitDurationInMinutes: 1
          rateLimitThreshold: 300
          action: 'Block'
          matchConditions: [
            {
              matchVariable: 'RequestUri'
              operator: 'Contains'
              matchValue: ['/api/']
              negateCondition: false
              transforms: []
            }
          ]
        }
      ]
    }
  }
}

resource securityPolicy 'Microsoft.Cdn/profiles/securityPolicies@2024-02-01' = {
  parent: profile
  name: 'waf'
  properties: {
    parameters: {
      type: 'WebApplicationFirewall'
      wafPolicy: { id: waf.id }
      associations: [{ domains: [{ id: endpoint.id }], patternsToMatch: ['/*'] }]
    }
  }
}

resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: profile
  name: 'front-door-logs'
  properties: {
    workspaceId: workspaceId
    logs: [
      { category: 'FrontDoorAccessLog', enabled: true }
      { category: 'FrontDoorWebApplicationFirewallLog', enabled: true }
      { category: 'FrontDoorHealthProbeLog', enabled: true }
    ]
  }
}

output hostName string = endpoint.properties.hostName
