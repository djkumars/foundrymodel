# Foundry Chatbot API

ASP.NET Core Web API that validates chatbot requests and calls the configured Microsoft Foundry chat-completions deployment.

## Requirements

- .NET 10 SDK
- A Microsoft Foundry deployment that supports the OpenAI-compatible chat-completions API
- An Azure Cosmos DB for NoSQL account, database, and container
- An Azure AI Content Safety resource with Prompt Shields support
- A Microsoft Entra External ID tenant with SPA and API registrations

## Configure local development

The project loads .NET User Secrets in Development. Set the deployment endpoint, key, and model name:

```sh
dotnet user-secrets set "Foundry:Endpoint" "https://YOUR-RESOURCE.openai.azure.com/openai/v1/chat/completions" --project foundry-chatbot-api.csproj
dotnet user-secrets set "Foundry:ApiKey" "YOUR-RESOURCE-KEY" --project foundry-chatbot-api.csproj
dotnet user-secrets set "Foundry:Model" "YOUR-DEPLOYMENT-NAME" --project foundry-chatbot-api.csproj
dotnet user-secrets set "CosmosDb:Endpoint" "https://YOUR-COSMOS-ACCOUNT.documents.azure.com:443/" --project foundry-chatbot-api.csproj
dotnet user-secrets set "CosmosDb:Key" "YOUR-COSMOS-KEY" --project foundry-chatbot-api.csproj
dotnet user-secrets set "CosmosDb:DatabaseName" "chatbot" --project foundry-chatbot-api.csproj
dotnet user-secrets set "CosmosDb:UserContainerName" "user-conversations" --project foundry-chatbot-api.csproj
dotnet user-secrets set "PromptShield:Endpoint" "https://YOUR-CONTENT-SAFETY-RESOURCE.cognitiveservices.azure.com/" --project foundry-chatbot-api.csproj
dotnet user-secrets set "PromptShield:ApiKey" "YOUR-CONTENT-SAFETY-KEY" --project foundry-chatbot-api.csproj
dotnet user-secrets set "ExternalId:TenantId" "YOUR-TENANT-GUID" --project foundry-chatbot-api.csproj
dotnet user-secrets set "ExternalId:Authority" "https://YOUR-TENANT.ciamlogin.com/YOUR-TENANT-GUID/v2.0" --project foundry-chatbot-api.csproj
dotnet user-secrets set "ExternalId:Audience" "YOUR-API-APPLICATION-GUID" --project foundry-chatbot-api.csproj
```

User Secrets are stored outside this project and are not committed. For deployment, provide the equivalent configuration using environment variables:

- `Foundry__Endpoint`
- `Foundry__ApiKey`
- `Foundry__Model`
- `CosmosDb__Endpoint`
- `CosmosDb__Key`
- `CosmosDb__DatabaseName`
- `CosmosDb__UserContainerName`
- `PromptShield__Endpoint`
- `PromptShield__ApiKey`
- `ExternalId__TenantId`
- `ExternalId__Authority`
- `ExternalId__Audience`

Create a **new** Cosmos DB for NoSQL container with partition key **`/ownerId`** before running the API. The API does not create Azure resources. Earlier anonymous containers used `/conversationId`; those records are not migrated or automatically assigned to any account. If the previous `CosmosDb:ContainerName` is still configured, the new `UserContainerName` must be different. Never place Foundry or Cosmos keys in browser configuration.

## Configure identity

1. Create an [External ID tenant](https://learn.microsoft.com/entra/external-id/customers/how-to-create-external-tenant-portal), register a single-tenant SPA and API in it, and add the SPA to a [sign-up/sign-in user flow](https://learn.microsoft.com/entra/external-id/customers/how-to-user-flow-sign-up-sign-in-customers).
2. Register `http://localhost:4200/auth-callback.html` as a **Single-page application** redirect URI for the SPA (and the equivalent HTTPS URI for each deployed chat origin). Allow that URI as a post-logout redirect as required by the tenant.
3. In the API registration, expose the delegated scope `access_as_user` using application ID URI `api://YOUR-API-APPLICATION-GUID`. Set the API registration's requested access token version to **2** (`api.requestedAccessTokenVersion` in its manifest). Add that delegated API permission to the SPA registration and grant consent as required.
4. Configure the API's three `ExternalId` values above and the Angular `public/auth-config.json` as shown in the [frontend guide](../chatbot-plugin/README.md). Tenant and application IDs are public identifiers, not credentials.

Chat endpoints require a valid access token for this API, with the configured tenant, a user object ID, and the `access_as_user` scope. The server derives the storage owner from validated claims, not from a browser-provided owner ID. Missing identity configuration fails closed: chat endpoints return 401. The unauthenticated health endpoint remains available.

## Model administration

The admin page is available at `/admin` on the frontend. Allow administrators by setting
`Admin:AllowedObjectIds` to an array of Entra External ID user object IDs (not application IDs),
for example through `Admin__AllowedObjectIds__0` and `Admin__AllowedObjectIds__1` environment
variables. With no valid allowlist entries, nobody can access the settings API. Administrators
must also meet the chat access policy. Sign in using the same External ID tenant as the chatbot.

Temperature is shared by all users and stored in the existing Cosmos container under a reserved
`system:model-settings` partition. Each new model request reads the setting from Cosmos; storage
failures prevent a model request rather than silently reverting to defaults. The default option
omits `temperature`, preserving the model's existing default. Explicit values must be between
0 and 2, but deployments may support a narrower range or not support temperature at all. Reset
to the model default if a deployment rejects the parameter. Changing this setting can affect
response quality and cost. Cosmos reads add request units and latency.

The Prompt Shields endpoint is the HTTPS root endpoint of the Content Safety resource, not the chat-completions URL. Prompt Shields receives each incoming user message and each prior user message included in model context. Each screening request has a 10-second timeout. Detected attacks return HTTP 400, and missing configuration, timeouts, service failures, or invalid results return HTTP 503; neither case calls the chat model or saves the exchange. Rechecking history adds latency and Content Safety usage charges. A blocked historical message prevents continuing that conversation; the chat window offers a new-conversation action.

The trusted system prompt is defined in `Prompts/ChatPromptTemplate.cs`. User messages remain separate lower-trust messages, rather than being interpolated into the system prompt. Prompt Shields is not a guarantee against prompt injection or multi-turn attacks; evaluate it with representative inputs and use additional controls appropriate to the application. Older stored messages are screened before reuse.

## Run

```sh
dotnet restore
dotnet run
```

The development profile listens on `http://localhost:3000`.

Run the API tests from the repository root with `dotnet test foundry-chatbot-api.Tests/foundry-chatbot-api.Tests.csproj`.

## Endpoints

- `GET /api/health` returns `{ "status": "ok" }`.
- `GET /api/admin/model-settings` and `PUT /api/admin/model-settings` read and update the
  shared setting for authorized administrators. The body is `{ "temperature": 0.7 }` or
  `{ "temperature": null }` to use the model default.
- `GET /api/chat/conversations?query=...&continuationToken=...` lists recent conversations when `query` is empty, or searches message content case-insensitively. A search returns message hits, so a conversation can appear more than once. Follow the returned `continuationToken` to retrieve more results with the same query. Each Cosmos page may contain fewer than 25 items, including zero.
- `GET /api/chat/{conversationId}?continuationToken=...` loads a page of up to 50 messages in chronological order, including saved assistant `tokenUsage`. Follow the returned continuation token to retrieve earlier pages.
- `POST /api/chat` accepts `{ "conversationId": "GUID", "message": "..." }`, saves the user/assistant exchange to Cosmos DB, and returns `{ "conversationId": "GUID", "reply": "...", "tokenUsage": { "promptTokens": 10, "completionTokens": 5, "totalTokens": 15 } }`.
- `POST /api/chat/stream` accepts the same request and streams `text/event-stream` events: `delta` with `{ "text": "..." }`, followed by `done` with `{ "conversationId": "...", "tokenUsage": { "promptTokens": 10, "completionTokens": 5, "totalTokens": 15 } }` once the exchange is saved. An `error` event with `{ "error": "..." }` indicates a failure after streaming has started; before streaming starts, errors use normal HTTP status codes and JSON. Partial exchanges are not saved, but if the connection is lost after saving, the client may not receive `done`.

The streaming request asks Foundry for usage via `stream_options.include_usage`. The deployment must support this option. Token usage is `null` when the service does not provide it, including for conversations saved before this feature; no token counts are estimated.

Chat requests are validated and rate-limited per authenticated user, or per client IP for unauthenticated requests. Limits are local to each API instance. The API uses the latest 19 stored messages plus the current user message alongside the system prompt, and saves each exchange atomically in the user's Cosmos DB partition. Cosmos search requires indexing of the queried properties and consumes request units. Search terms are sent in the URL; consider access-log and proxy retention policies when handling sensitive conversations.
The streaming request has a 30-second overall Foundry timeout. Configure production reverse proxies to disable response buffering for this endpoint.
