using foundry_chatbot_api.Models;

namespace foundry_chatbot_api.Services;

public interface IConversationStore
{
    bool IsConfigured { get; }

    Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
        string ownerId,
        string conversationId,
        CancellationToken cancellationToken);

    Task<ChatHistoryResponse> GetHistoryPageAsync(
        string ownerId,
        string conversationId,
        string? continuationToken,
        CancellationToken cancellationToken);

    Task<ConversationSearchResponse> SearchAsync(
        string ownerId,
        string query,
        string? continuationToken,
        CancellationToken cancellationToken);

    Task SaveExchangeAsync(
        string ownerId,
        string conversationId,
        string userMessage,
        string assistantMessage,
        TokenUsage? tokenUsage,
        CancellationToken cancellationToken);
}
