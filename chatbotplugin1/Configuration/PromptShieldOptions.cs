namespace foundry_chatbot_api.Configuration;

public sealed class PromptShieldOptions
{
    public const string SectionName = "PromptShield";

    public string? Endpoint { get; set; }

    public string? ApiKey { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiKey) &&
        Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) &&
        endpoint.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(endpoint.UserInfo) &&
        string.IsNullOrEmpty(endpoint.Query) &&
        string.IsNullOrEmpty(endpoint.Fragment) &&
        endpoint.AbsolutePath == "/";
}
