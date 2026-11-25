namespace foundry_chatbot_api.Models;

/// <summary>A page of conversations or matching messages.</summary>
public sealed record ConversationSearchResponse(
    IReadOnlyList<ConversationSearchItem> Items,
    string? ContinuationToken);

/// <summary>A conversation and its recent or matching message preview.</summary>
public sealed record ConversationSearchItem(
    string ConversationId,
    string Preview,
    DateTimeOffset UpdatedAt);
