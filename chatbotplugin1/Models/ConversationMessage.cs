namespace foundry_chatbot_api.Models;

public sealed class ConversationMessage
{
    public string Id { get; set; } = string.Empty;

    public string ConversationId { get; set; } = string.Empty;

    public string OwnerId { get; set; } = string.Empty;

    public string DocumentType { get; set; } = "message";

    public string Role { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public TokenUsage? TokenUsage { get; set; }
}
