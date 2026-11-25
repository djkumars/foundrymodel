namespace foundry_chatbot_api.Models;

public sealed record ChatHistoryResponse(
    string ConversationId,
    IReadOnlyList<ChatHistoryMessage> Messages,
    string? ContinuationToken = null);

public sealed record ChatHistoryMessage(
    string Id,
    string Role,
    string Content,
    DateTimeOffset CreatedAt,
    TokenUsage? TokenUsage);
