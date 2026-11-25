# Foundry website chatbot

This solution contains two independent projects:

- **Angular website and chat widget** — this folder's `src/` and `public/`.
- **.NET 10 ASP.NET Core Web API** — [`foundry-chatbot-api`](../foundry-chatbot-api), stored outside the Angular plugin, which validates browser requests and calls Microsoft Foundry using server-only credentials.

The browser calls `/api/chat`; it never calls Foundry directly or receives the Foundry key.

## Requirements

- Node.js 22.12 or newer
- .NET 10 SDK
- A Microsoft Foundry model deployment that supports the OpenAI-compatible chat-completions API
- An Azure AI Content Safety resource with Prompt Shields support
- A Microsoft Entra External ID tenant with SPA and API registrations

## Configure the API

Set local credentials using .NET User Secrets from the API project folder:

```sh
cd ../foundry-chatbot-api
dotnet user-secrets set "Foundry:Endpoint" "https://YOUR-RESOURCE.openai.azure.com/openai/v1/chat/completions"
dotnet user-secrets set "Foundry:ApiKey" "YOUR-RESOURCE-KEY"
dotnet user-secrets set "Foundry:Model" "YOUR-DEPLOYMENT-NAME"
cd ../chatbot-plugin
```

User Secrets are stored outside the projects. For deployment, set `Foundry__Endpoint`, `Foundry__ApiKey`, and `Foundry__Model` as server environment variables or hosting-platform secrets. Never put the key in Angular files, browser code, or the embed snippet.

Conversation persistence also needs an Azure Cosmos DB for NoSQL database and a **new** container with partition key `/ownerId`. Configure `CosmosDb:Endpoint`, `CosmosDb:Key`, `CosmosDb:DatabaseName`, and `CosmosDb:UserContainerName` on the API. Previous anonymous conversations are not migrated. See the [API setup guide](../foundry-chatbot-api/README.md) for the full commands and identity registration steps.

Configure `PromptShield:Endpoint` and `PromptShield:ApiKey` on the API server as described in the API setup guide. Prompt Shields screens incoming and prior user messages before Foundry is called; the API blocks requests if screening detects an attack or is unavailable. Detection is not guaranteed to catch all attacks.

## Configure sign-in

After creating the External ID tenant and SPA/API registrations described in the API guide, fill in `public/auth-config.json` with public identifiers:

```json
{
  "clientId": "YOUR-SPA-APPLICATION-GUID",
  "authority": "https://YOUR-TENANT.ciamlogin.com/YOUR-TENANT-GUID",
  "apiScope": "api://YOUR-API-APPLICATION-GUID/access_as_user",
  "useStreaming": true
}
```

Register `http://localhost:4200/auth-callback.html` as the SPA redirect URI. Deploy the callback page, its script, and the MSAL redirect-bridge asset together with the frontend; register the corresponding HTTPS callback for each production origin. Configure the matching API `ExternalId` settings separately. The empty placeholders deliberately disable sign-in until configured.
`useStreaming` defaults to `true` when omitted. Set it to `false` when serving the API through Azure Front Door: Front Door does not support SSE, so the widget uses the non-streaming JSON endpoint instead.

## Run locally

Install the Angular dependencies:

```sh
npm install
```

In one terminal, start the API:

```sh
npm run start:api
```

This runs the API project in `../foundry-chatbot-api`.

In another terminal, start the Angular website:

```sh
npm start
```

Open `http://localhost:4200`. The floating chat icon opens the assistant; the standalone chat is at `http://localhost:4200/chat`. Angular's development proxy forwards `/api` requests to the API on port 3000.

Administrators can configure the shared model temperature at `http://localhost:4200/admin`.
Configure the API's administrator allowlist first; see [model administration](../foundry-chatbot-api/README.md#model-administration).

## API project

The ASP.NET Core API reads Foundry and Cosmos DB settings from .NET User Secrets in Development. For production, configure them as environment variables or hosting-platform secrets. The API provides:

- `GET /api/chat/conversations` — lists the signed-in user's conversations or searches their message contents; supports continuation tokens.
- `GET /api/chat/{conversationId}` — restores a page of stored messages, with continuation tokens for earlier messages.
- `POST /api/chat` — accepts a conversation ID and new message; the API loads prior messages from Cosmos DB, calls Foundry, then atomically stores the user and assistant messages.
- `POST /api/chat/stream` — streams assistant text to the widget and reports completion only after the exchange is saved. Configure reverse proxies not to buffer this response.
- `GET /api/health` — liveness check.

The widget displays Foundry's input, output, and total token usage beneath each completed assistant reply. Usage is saved with the conversation; older messages or responses without usage show it as unavailable.
Users can search message text, open matching conversations, and load older pages. Search results may contain multiple matches for a conversation.

Build the API with `dotnet build ../foundry-chatbot-api/foundry-chatbot-api.csproj`. The local API listens on port 3000, matching the Angular development proxy.

## Embed on another website

Build the Angular website with `npm run build` and host the generated files over HTTPS. Publish and host `../foundry-chatbot-api` as a separate service. Configure the hosting reverse proxy or API gateway so `/api/*` on the chat website's origin routes to the API service; this preserves the widget's same-origin API URL and avoids exposing API credentials. Configure the Foundry settings as secrets on the API host.

Add the widget loader to the website where the chatbot should appear:

```html
<script
  src="https://YOUR-CHAT-HOST.example.com/widget-loader.js"
  data-title="Website assistant"
  data-button-text="Chat with us"
  defer
></script>
```

The loader creates a floating launcher and an iframe for `/chat`. To host the chat page under a different URL, set `data-chat-url` to its absolute URL. Sign-in uses a popup and browser session storage; embedded use may be limited by popup blockers, cross-site storage restrictions, and iframe policies. Test the intended browsers and embedding hosts.

For production, use HTTPS, configure rate limiting appropriate to your traffic (and a shared store for multiple API instances), and store Foundry credentials in the hosting platform's secret store. Use server-side managed identity where supported.
