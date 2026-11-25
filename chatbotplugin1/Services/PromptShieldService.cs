using System.Net.Http.Json;
using System.Text.Json;
using foundry_chatbot_api.Configuration;
using Microsoft.Extensions.Options;

namespace foundry_chatbot_api.Services;

public sealed class PromptShieldService(
    HttpClient httpClient,
    IOptions<PromptShieldOptions> options,
    ILogger<PromptShieldService> logger) : IPromptShieldService
{
    private readonly PromptShieldOptions _options = options.Value;

    public bool IsConfigured => _options.IsConfigured;

    public async Task<bool> IsAttackAsync(string prompt, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Prompt Shields is not configured.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var endpoint = new Uri(new Uri(_options.Endpoint!), "/contentsafety/text:shieldPrompt?api-version=2024-09-01");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new { userPrompt = prompt, documents = Array.Empty<string>() })
        };
        request.Headers.Add("Ocp-Apim-Subscription-Key", _options.ApiKey);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Prompt Shields request failed with status {StatusCode}.", (int)response.StatusCode);
            throw new HttpRequestException("Prompt Shields returned an unsuccessful response.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("userPromptAnalysis", out var analysis) ||
            analysis.ValueKind != JsonValueKind.Object ||
            !analysis.TryGetProperty("attackDetected", out var detected) ||
            detected.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new JsonException("Prompt Shields returned an invalid response.");
        }

        return detected.GetBoolean();
    }
}
