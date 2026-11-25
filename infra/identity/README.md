# External ID for the chatbot

This directory is **identity only**. It contains a separate preview Bicep
template for tenant creation and an opt-in Microsoft Graph registration script.
The sign-up/sign-in flow and consent remain manual. No subscription, tenant,
Graph operation, or live authentication was available for verification. Treat
both the template and script as unverified until tested with the provider.

## Prerequisites and decisions

1. Create a **new External** tenant using the [Entra admin center](https://learn.microsoft.com/en-us/entra/external-id/customers/how-to-create-external-tenant-portal), or review and deploy `tenant.bicep` separately.
   The paid path requires an Azure subscription and Tenant Creator scoped to
   the subscription or resource group; first-time trial eligibility differs.
   Choose a unique initial domain and the appropriate **country/region**; this
   choice is irreversible. East US 2 is the requested Azure resource-group
   location, not a guarantee of Entra tenant data residency. Record tenant GUID
   and initial `<label>.onmicrosoft.com` domain.
   The tenant resource uses the preview `2023-05-17-preview` API. Microsoft's
   [resource reference](https://learn.microsoft.com/azure/templates/microsoft.azureactivedirectory/ciamdirectories)
   and [published sample](https://github.com/Azure-Samples/azure-health-data-and-ai-samples/blob/main/samples/fhir-aad-entra-external/entra-external-arm-template.json)
   currently disagree on the resource name format and SKU (`Standard` versus
   `Base`). Confirm the provider's current accepted values for your subscription
   before deployment; `directoryResourceName` and `tenantSkuName` intentionally
   have no defaults. Do not deploy until the irreversible residency choice and
   billing have been approved.
   The following previews and then creates a tenant only after you replace the
   placeholders and confirm the provider's values:

   ```sh
   az deployment group what-if --resource-group '<resource-group>' \
     --template-file infra/identity/tenant.bicep \
     --parameters directoryResourceName='<verified-resource-name>' \
       displayName='<display-name>' countryCode='<country-code>' \
       tenantDataLocation='<tenant-data-location>' tenantSkuName='<verified-sku>'
   az deployment group create --resource-group '<resource-group>' \
     --template-file infra/identity/tenant.bicep \
     --parameters directoryResourceName='<verified-resource-name>' \
       displayName='<display-name>' countryCode='<country-code>' \
       tenantDataLocation='<tenant-data-location>' tenantSkuName='<verified-sku>'
   ```
2. Switch to that external tenant and confirm it is a customer tenant. Graph's
   organization check verifies GUID and initial domain, **not tenant type**.
   Use Python 3 and a short-lived **delegated Microsoft Graph access token** for
   that tenant, with `Application.ReadWrite.All` and `User.Read` (or
   `Organization.Read.All`) consented and an appropriate role/ownership to
   create and update apps and service principals. Consult the Graph operation
   docs below. Obtain the token through an approved interactive Graph client;
   never save it to a file, command history, source, or CI logs. The script
   prompts without echo; it does not obtain, refresh, or store tokens.
3. Decide trusted SPA origins. Supply the actual Front Door **frontend**
   HTTPS host once known, not its public origin hostname or the API host.
   Each exact redirect is `<origin>/auth-callback.html`. For development add
   `http://localhost:4200/auth-callback.html` explicitly; omit it in production
   registrations if not needed. Wildcards are not supported. The script does
   not create Front Door, configure origin access, or deploy GPT-4o.
4. Choose unique app display names, a user-flow authentication method and
   attributes, and a consent policy. Do not treat display names as unique keys.
   Customer MAU pricing and subscription charges may apply.

## Registration

The following is an **offline plan**; replace all placeholders, including the
example domain, before use:

```sh
python3 infra/identity/provision.py \
  --tenant-id 00000000-0000-0000-0000-000000000001 \
  --tenant-label exampletenant \
  --api-name 'Chat API prod' --spa-name 'Chat SPA prod' \
  --redirect-uri https://chat.example.com/auth-callback.html
```

Add further `--redirect-uri` arguments as needed. To actually provision later,
run the same command with `--apply`; paste the Graph access token at the prompt.
This sends the token only to `graph.microsoft.com` over HTTPS; redirects are
rejected. The script checks the tenant and exact app names, creates the API
registration, sets `api://<API-appId>`, a new `access_as_user` delegated scope
and v2 tokens, creates its service principal, creates the single-tenant SPA
registration with the scope and redirects, then creates its service principal.
It prints IDs and app configuration, not credentials. It does not grant consent.

**Create-only, not reconciliation**: no existing app is modified intentionally,
and matching names stop execution; checks are not concurrency locks. Do not
run concurrent copies. A network timeout, throttling, permission failure or
interruption can leave partially created registrations/service principals,
including an unreported object if a POST succeeded but its response was lost.
Inspect both app registrations and enterprise applications in the external
tenant before retrying. Record printed IDs securely; either complete setup
manually or remove only the objects you positively identify as created by this
run before restarting. No automatic retries, rollback, adoption, or deletion
are performed. Allow for directory propagation; if a step fails, inspect the
actual state rather than blindly rerunning. The output is public configuration,
but handle identifiers according to your organization's disclosure policy.

## Finish in Entra and configure the app

1. [Create a sign-up/sign-in user flow](https://learn.microsoft.com/en-us/entra/external-id/customers/how-to-user-flow-sign-up-sign-in-customers),
   choose the approved email authentication method and minimum attributes, and
   [associate the SPA](https://learn.microsoft.com/en-us/entra/external-id/customers/how-to-user-flow-add-application).
   Enable email OTP at tenant level first if using it. One app can have only one
   user flow. Do not delete `b2c-extensions-app`.
2. Inspect both app registrations and service principals in the external
   tenant. Confirm API scope is enabled, SPA permission is delegated, and grant
   consent as required by tenant policy. This is deliberately manual: choosing
   tenant-wide admin consent is a security decision. Verify the callback is
   also accepted for the SPA's popup logout behavior in this tenant.
3. Set the script's frontend JSON as the deployed `/auth-config.json` and the
   API values as server environment variables (`ExternalId__TenantId`,
   `ExternalId__Authority`, `ExternalId__Audience`). No secrets are involved in
   these values. The Angular app requires the authority without `/v2.0`; .NET
   requires it with `/v2.0` and uses the API app ID as audience.
   For the Front Door infrastructure, add `"useStreaming": false` to the
   frontend JSON; the printed registration configuration does not set it.
4. Live acceptance test: exercise signup, login, silent token retrieval and
   popup logout on each intended origin and embedding browser. Confirm a v2
   token has the expected `aud`, `tid`, user `oid`, and `scp` containing
   `access_as_user` without logging the token. Confirm an authorized chat
   request works, missing/wrong-audience/wrong-tenant/scope-less tokens fail,
   and `/api/health` remains anonymous. The chat API currently authorizes by
   tenant, user ID and scope, **not by client app ID**. Tokens from other
   consented clients in the same tenant with that scope may be accepted.
   API calls use `/api` on the frontend origin; configure routing separately.

## Local checks

```sh
python3 -B -m unittest discover -s infra/identity -p 'test_*.py' -v
```

These are offline mock tests only, not a Graph integration or deployability
test. No packages or Azure operations are required.

## References

- [Graph create application](https://learn.microsoft.com/en-us/graph/api/application-post-applications?view=graph-rest-1.0),
  [update application](https://learn.microsoft.com/en-us/graph/api/application-update?view=graph-rest-1.0),
  [create service principal](https://learn.microsoft.com/en-us/graph/api/serviceprincipal-post-serviceprincipals?view=graph-rest-1.0),
  [organization](https://learn.microsoft.com/en-us/graph/api/organization-list?view=graph-rest-1.0).
- [Delegated permission scope](https://learn.microsoft.com/en-us/graph/api/resources/permissionscope?view=graph-rest-1.0)
  and [registration security guidance](https://learn.microsoft.com/en-us/entra/identity-platform/security-best-practices-for-app-registration).
