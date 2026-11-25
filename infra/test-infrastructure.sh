#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
compiled="$(bicep build "$root/infra/main.bicep" --stdout)"
bicep build "$root/infra/identity/tenant.bicep" --stdout > /dev/null

printf '%s' "$compiled" | jq -e '
  [.. | objects | select(.type? == "Microsoft.Web/sites")] as $apps |
  [.. | objects | select(.type? == "Microsoft.DocumentDB/databaseAccounts")] as $accounts |
  [.. | objects | select(.type? == "Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers")] as $containers |
  [.. | objects | select(.type? == "Microsoft.CognitiveServices/accounts")] as $ai |
  [.. | objects | select(.type? == "Microsoft.KeyVault/vaults")] as $vaults |
  [.. | objects | select(.type? == "Microsoft.Cdn/profiles/afdEndpoints/routes")] as $routes |
  [.. | objects | select(.type? == "Microsoft.Network/FrontDoorWebApplicationFirewallPolicies")] as $wafs |
  ($apps | length == 1) and
  ($apps[0].properties.httpsOnly == true) and
  ($apps[0].properties.siteConfig.ipSecurityRestrictionsDefaultAction == "Deny") and
  ($apps[0].properties.siteConfig.scmIpSecurityRestrictionsDefaultAction == "Deny") and
  ($apps[0].properties.siteConfig.ipSecurityRestrictions[0].ipAddress == "AzureFrontDoor.Backend") and
  ($apps[0].properties.siteConfig.ipSecurityRestrictions[0].headers["x-azure-fdid"] | length == 1) and
  ($accounts | length == 1) and
  ($accounts[0].properties.isVirtualNetworkFilterEnabled == true) and
  ($accounts[0].properties.virtualNetworkRules | length == 1) and
  ($containers[0].properties.resource.partitionKey.paths == ["/ownerId"]) and
  ($ai | length == 2) and
  (all($ai[]; .properties.networkAcls.defaultAction == "Deny" and (.properties.networkAcls.virtualNetworkRules | length == 1))) and
  ($vaults[0].properties.enableRbacAuthorization == true) and
  ($vaults[0].properties.networkAcls.defaultAction == "Deny") and
  ($routes | length == 2) and
  (any($routes[]; .properties.patternsToMatch == ["/api/*"] and (.properties | has("cacheConfiguration") | not))) and
  ($wafs[0].properties.policySettings.mode == "Prevention") and
  (any($wafs[0].properties.customRules.rules[]; .ruleType == "RateLimitRule" and .action == "Block"))
' > /dev/null

echo 'Bicep compilation and offline security checks passed.'
