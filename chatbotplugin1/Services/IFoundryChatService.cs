using foundry_chatbot_api.Models;

namespace foundry_chatbot_api.Services;

public interface IFoundryChatService
{
    bool IsConfigured { get; }

    Task<FoundryChatReply> GetReplyAsync(
        IReadOnlyCollection<ChatMessage> messages,
        CancellationToken cancellationToken);

    IAsyncEnumerable<FoundryStreamChunk> StreamReplyAsync(
        IReadOnlyCollection<ChatMessage> messages,
        CancellationToken cancellationToken);
}
