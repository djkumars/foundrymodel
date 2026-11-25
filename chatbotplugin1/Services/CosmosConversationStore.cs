using foundry_chatbot_api.Configuration;
using foundry_chatbot_api.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

namespace foundry_chatbot_api.Services;

public sealed class CosmosConversationStore : IConversationStore, IDisposable
{
    private readonly CosmosDbOptions _options;
    private readonly CosmosClient? _client;
    private readonly ILogger<CosmosConversationStore> _logger;

    public CosmosConversationStore(
        IOptions<CosmosDbOptions> options,
        ILogger<CosmosConversationStore> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (_options.IsConfigured &&
            Uri.TryCreate(_options.Endpoint, UriKind.Absolute, out var endpoint) &&
            endpoint.Scheme == Uri.UriSchemeHttps)
        {
            _client = new CosmosClient(
                endpoint.ToString(),
                _options.Key,
                new CosmosClientOptions
                {
                    SerializerOptions = new CosmosSerializationOptions
                    {
                        PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
                    }
                });
        }
    }

    public bool IsConfigured => _client is not null;

    public async Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
        string ownerId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        var container = GetContainer();
        var query = new QueryDefinition(
                "SELECT TOP 100 * FROM c WHERE c.documentType = 'message' AND c.conversationId = @conversationId ORDER BY c.createdAt DESC")
            .WithParameter("@conversationId", conversationId);
        var iterator = container.GetItemQueryIterator<ConversationMessage>(
            query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(ownerId),
                MaxItemCount = 100
            });

        var messages = new List<ConversationMessage>();
        while (iterator.HasMoreResults && messages.Count < 100)
        {
            var page = await iterator.ReadNextAsync(cancellationToken);
            messages.AddRange(page);
        }

        messages.Reverse();
        return messages;
    }

    public async Task<ConversationSearchResponse> SearchAsync(
        string ownerId,
        string query,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        var sql = string.IsNullOrEmpty(query)
            ? "SELECT c.conversationId, c.preview, c.updatedAt FROM c WHERE c.documentType = 'conversation' ORDER BY c.updatedAt DESC"
            : "SELECT c.conversationId, c.content AS preview, c.createdAt AS updatedAt FROM c WHERE c.documentType = 'message' AND CONTAINS(c.content, @query, true) ORDER BY c.createdAt DESC";
        var definition = new QueryDefinition(sql);
        if (!string.IsNullOrEmpty(query)) definition.WithParameter("@query", query);
        var iterator = GetContainer().GetItemQueryIterator<ConversationSearchItem>(
            definition,
            continuationToken,
            new QueryRequestOptions { PartitionKey = new PartitionKey(ownerId), MaxItemCount = 25 });
        if (!iterator.HasMoreResults) return new ConversationSearchResponse([], null);
        var page = await iterator.ReadNextAsync(cancellationToken);
        var items = page.Select(item => item with
        {
            Preview = item.Preview.Length > 160 ? item.Preview[..160] + "..." : item.Preview
        }).ToArray();
        return new ConversationSearchResponse(items, page.ContinuationToken);
    }

    public async Task<ChatHistoryResponse> GetHistoryPageAsync(
        string ownerId,
        string conversationId,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.documentType = 'message' AND c.conversationId = @conversationId ORDER BY c.createdAt DESC")
            .WithParameter("@conversationId", conversationId);
        var iterator = GetContainer().GetItemQueryIterator<ConversationMessage>(
            query,
            continuationToken,
            new QueryRequestOptions { PartitionKey = new PartitionKey(ownerId), MaxItemCount = 50 });
        if (!iterator.HasMoreResults) return new ChatHistoryResponse(conversationId, [], null);
        var page = await iterator.ReadNextAsync(cancellationToken);
        var messages = page.Reverse().Select(message => new ChatHistoryMessage(
            message.Id, message.Role, message.Content, message.CreatedAt, message.TokenUsage)).ToArray();
        return new ChatHistoryResponse(conversationId, messages, page.ContinuationToken);
    }

    public async Task SaveExchangeAsync(
        string ownerId,
        string conversationId,
        string userMessage,
        string assistantMessage,
        TokenUsage? tokenUsage,
        CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var batch = GetContainer()
            .CreateTransactionalBatch(new PartitionKey(ownerId))
            .CreateItem(new ConversationMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                OwnerId = ownerId,
                ConversationId = conversationId,
                Role = "user",
                Content = userMessage,
                CreatedAt = timestamp
            })
            .CreateItem(new ConversationMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                OwnerId = ownerId,
                ConversationId = conversationId,
                Role = "assistant",
                Content = assistantMessage,
                CreatedAt = timestamp.AddTicks(1),
                TokenUsage = tokenUsage
            })
            .UpsertItem(new ConversationSummaryDocument
            {
                Id = $"conversation-{conversationId}",
                OwnerId = ownerId,
                ConversationId = conversationId,
                Preview = userMessage.Length > 160 ? userMessage[..160] + "..." : userMessage,
                UpdatedAt = timestamp
            });

        using var response = await batch.ExecuteAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Saving a conversation exchange to Cosmos DB failed with status {StatusCode}.",
                (int)response.StatusCode);
            throw new CosmosException(
                "Cosmos DB could not save the conversation.",
                response.StatusCode,
                0,
                response.ActivityId,
                response.RequestCharge);
        }
    }

    public void Dispose() => _client?.Dispose();

    private Container GetContainer()
    {
        if (_client is null)
        {
            throw new InvalidOperationException("Cosmos DB conversation storage is not configured.");
        }

        return _client.GetContainer(_options.DatabaseName, _options.UserContainerName);
    }

    private sealed class ConversationSummaryDocument
    {
        public string Id { get; set; } = string.Empty;
        public string OwnerId { get; set; } = string.Empty;
        public string ConversationId { get; set; } = string.Empty;
        public string DocumentType { get; set; } = "conversation";
        public string Preview { get; set; } = string.Empty;
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
