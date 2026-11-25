namespace foundry_chatbot_api.Configuration;

public sealed class CosmosDbOptions
{
    public const string SectionName = "CosmosDb";

    public string? Endpoint { get; set; }

    public string? Key { get; set; }

    public string? DatabaseName { get; set; }

    public string? ContainerName { get; set; }

    public string? UserContainerName { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint) &&
        !string.IsNullOrWhiteSpace(Key) &&
        !string.IsNullOrWhiteSpace(DatabaseName) &&
        !string.IsNullOrWhiteSpace(UserContainerName) &&
        !string.Equals(ContainerName, UserContainerName, StringComparison.OrdinalIgnoreCase);
}
