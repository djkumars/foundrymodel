using System.Security.Claims;
using foundry_chatbot_api.Controllers;
using foundry_chatbot_api.Models;
using foundry_chatbot_api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace foundry_chatbot_api.Tests;

public class ConversationAccessTests
{
    private const string Tenant = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Alice = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string Bob = "cccccccc-cccc-cccc-cccc-cccccccccccc";

    [Fact]
    public async Task HistoryAndSearchAreScopedToAuthenticatedOwner()
    {
        var store = new MemoryStore();
        var alice = Controller(store, Alice);
        var bob = Controller(store, Bob);
        var id = Guid.NewGuid();

        await alice.PostMessage(new ChatRequest(id.ToString(), "private message"), CancellationToken.None);
        var aliceHistory = Assert.IsType<ChatHistoryResponse>(
            Assert.IsType<OkObjectResult>(await alice.GetHistory(id, null, CancellationToken.None)).Value);
        var bobHistory = Assert.IsType<ChatHistoryResponse>(
            Assert.IsType<OkObjectResult>(await bob.GetHistory(id, null, CancellationToken.None)).Value);
        Assert.Single(aliceHistory.Messages);
        Assert.Empty(bobHistory.Messages);

        var aliceSearch = Assert.IsType<ConversationSearchResponse>(
            Assert.IsType<OkObjectResult>(await alice.SearchConversations("private", null, CancellationToken.None)).Value);
        var bobSearch = Assert.IsType<ConversationSearchResponse>(
            Assert.IsType<OkObjectResult>(await bob.SearchConversations("private", null, CancellationToken.None)).Value);
        Assert.Single(aliceSearch.Items);
        Assert.Empty(bobSearch.Items);
    }

    [Fact]
    public async Task MissingAuthenticatedOwnerCannotReadOrSearch()
    {
        var controller = Controller(new MemoryStore(), null);
        Assert.IsType<UnauthorizedResult>(await controller.GetHistory(Guid.NewGuid(), null, CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(await controller.SearchConversations("", null, CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(await controller.PostMessage(
            new ChatRequest(Guid.NewGuid().ToString(), "hello"), CancellationToken.None));
        var attribute = Assert.Single(typeof(ChatController).GetCustomAttributes(typeof(AuthorizeAttribute), true));
        Assert.Equal("ChatAccess", ((AuthorizeAttribute)attribute).Policy);
    }

    private static ChatController Controller(MemoryStore store, string? objectId)
    {
        var claims = objectId is null ? Array.Empty<Claim>() : [new Claim("tid", Tenant), new Claim("oid", objectId)];
        return new ChatController(new Model(), store, new Shield(), NullLogger<ChatController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, objectId is null ? null : "test"))
                }
            }
        };
    }

    private sealed class Shield : IPromptShieldService
    {
        public bool IsConfigured => true;
        public Task<bool> IsAttackAsync(string prompt, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class Model : IFoundryChatService
    {
        public bool IsConfigured => true;
        public Task<FoundryChatReply> GetReplyAsync(IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken) =>
            Task.FromResult(new FoundryChatReply("reply", null));
        public async IAsyncEnumerable<FoundryStreamChunk> StreamReplyAsync(
            IReadOnlyCollection<ChatMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new FoundryStreamChunk(Text: "reply");
        }
    }

    private sealed class MemoryStore : IConversationStore
    {
        private readonly Dictionary<(string Owner, string Conversation), string> messages = [];
        public bool IsConfigured => true;
        public Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(string ownerId, string conversationId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ConversationMessage>>(messages.TryGetValue((ownerId, conversationId), out var value)
                ? [new ConversationMessage { Role = "user", Content = value }] : []);
        public async Task<ChatHistoryResponse> GetHistoryPageAsync(string ownerId, string conversationId, string? continuationToken, CancellationToken cancellationToken)
        {
            var history = await GetMessagesAsync(ownerId, conversationId, cancellationToken);
            return new ChatHistoryResponse(conversationId,
                history.Select(m => new ChatHistoryMessage("id", m.Role, m.Content, DateTimeOffset.UtcNow, null)).ToArray());
        }
        public Task<ConversationSearchResponse> SearchAsync(string ownerId, string query, string? continuationToken, CancellationToken cancellationToken) =>
            Task.FromResult(new ConversationSearchResponse(
                messages.Where(item => item.Key.Owner == ownerId && item.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Select(item => new ConversationSearchItem(item.Key.Conversation, item.Value, DateTimeOffset.UtcNow)).ToArray(), null));
        public Task SaveExchangeAsync(string ownerId, string conversationId, string userMessage, string assistantMessage, TokenUsage? tokenUsage, CancellationToken cancellationToken)
        {
            messages[(ownerId, conversationId)] = userMessage;
            return Task.CompletedTask;
        }
    }
}
