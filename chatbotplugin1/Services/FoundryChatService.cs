using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using foundry_chatbot_api.Configuration;
using foundry_chatbot_api.Models;
using Microsoft.Extensions.Options;

namespace foundry_chatbot_api.Services;

public sealed class FoundryChatService(
    HttpClient httpClient,
    IOptions<FoundryOptions> options,
    IModelSettingsStore settingsStore,
    ILogger<FoundryChatService> logger) : IFoundryChatService
{
    private readonly FoundryOptions _options = options.Value;
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public bool IsConfigured => _options.IsConfigured;

    public async Task<FoundryChatReply> GetReplyAsync(
        IReadOnlyCollection<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(_options.Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new InvalidOperationException("The Foundry chat endpoint must be a valid HTTPS URL.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                model = _options.Model,
                messages,
                max_tokens = 800,
                temperature = await settingsStore.GetTemperatureAsync(cancellationToken)
            }, options: RequestJsonOptions)
        };
        request.Headers.Add("api-key", _options.ApiKey);

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Foundry chat request failed with status {StatusCode}.", (int)response.StatusCode);
            throw new HttpRequestException("The Foundry model returned an unsuccessful response.");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var result = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);

        if (result.RootElement.ValueKind != JsonValueKind.Object ||
            !result.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0 ||
            choices[0].ValueKind != JsonValueKind.Object ||
            !choices[0].TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(content.GetString()))
        {
            logger.LogError("Foundry chat response did not contain a text reply.");
            return new FoundryChatReply(null, null);
        }

        var usage = ReadUsage(result.RootElement);
        if (usage is null) logger.LogWarning("Foundry chat response did not contain token usage.");
        return new FoundryChatReply(content.GetString(), usage);
    }

    public async IAsyncEnumerable<FoundryStreamChunk> StreamReplyAsync(
        IReadOnlyCollection<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(_options.Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new InvalidOperationException("The Foundry chat endpoint must be a valid HTTPS URL.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                model = _options.Model,
                messages,
                max_tokens = 800,
                stream = true,
                stream_options = new { include_usage = true },
                temperature = await settingsStore.GetTemperatureAsync(timeout.Token)
            }, options: RequestJsonOptions)
        };
        request.Headers.Add("api-key", _options.ApiKey);

        using var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Foundry chat request failed with status {StatusCode}.", (int)response.StatusCode);
            throw new HttpRequestException("The Foundry model returned an unsuccessful response.");
        }

        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Foundry returned an unexpected streaming content type.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var receivedUsage = false;
        await foreach (var item in SseParser.Create(stream).EnumerateAsync(timeout.Token))
        {
            if (item.Data == "[DONE]")
            {
                if (!receivedUsage) logger.LogWarning("Foundry streaming response did not contain token usage.");
                yield break;
            }

            using var document = JsonDocument.Parse(item.Data);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.TryGetProperty("error", out _) ||
                !root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("Foundry returned an invalid streaming response.");
            }

            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("Foundry returned an invalid streaming choice.");
                }

                if (choice.TryGetProperty("finish_reason", out var reason) &&
                    reason.ValueKind == JsonValueKind.String &&
                    reason.GetString() == "content_filter")
                {
                    throw new InvalidDataException("Foundry filtered the response.");
                }

                if (choice.TryGetProperty("delta", out var delta) &&
                    delta.ValueKind == JsonValueKind.Object &&
                    delta.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrEmpty(content.GetString()))
                {
                    yield return new FoundryStreamChunk(Text: content.GetString());
                }
            }

            var usage = ReadUsage(root);
            if (usage is not null)
            {
                receivedUsage = true;
                yield return new FoundryStreamChunk(TokenUsage: usage);
            }
        }

        throw new InvalidDataException("Foundry streaming response ended before completion.");
    }

    private static TokenUsage? ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (usage.ValueKind != JsonValueKind.Object ||
            !TryReadTokenCount(usage, "prompt_tokens", out var prompt) ||
            !TryReadTokenCount(usage, "completion_tokens", out var completion) ||
            !TryReadTokenCount(usage, "total_tokens", out var total))
        {
            throw new JsonException("Foundry returned invalid token usage.");
        }

        return new TokenUsage(prompt, completion, total);
    }

    private static bool TryReadTokenCount(JsonElement usage, string name, out int count)
    {
        count = 0;
        return usage.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out count) &&
            count >= 0;
    }
}
