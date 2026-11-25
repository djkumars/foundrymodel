namespace foundry_chatbot_api.Configuration;

public sealed class FoundryOptions
{
    public const string SectionName = "Foundry";

    public string? Endpoint { get; set; }

    public string? ApiKey { get; set; }

    public string? Model { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint) &&
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(Model);
}
