using System.Net;
using System.Text;
using System.Text.Json;
using System.Security.Claims;
using foundry_chatbot_api.Configuration;
using foundry_chatbot_api.Controllers;
using foundry_chatbot_api.Models;
using foundry_chatbot_api.Prompts;
using foundry_chatbot_api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace foundry_chatbot_api.Tests;

public class PromptSecurityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShieldSendsExpectedRequestAndParsesResult(bool attack)
    {
        var handler = new ShieldHandler($"{{\"userPromptAnalysis\":{{\"attackDetected\":{attack.ToString().ToLowerInvariant()}}}}}");
        var service = CreateShield(handler);

        Assert.Equal(attack, await service.IsAttackAsync("hello", CancellationToken.None));
        Assert.Equal("/contentsafety/text:shieldPrompt?api-version=2024-09-01", handler.Path);
        Assert.Equal("test-key", handler.Key);
        using var request = JsonDocument.Parse(handler.Body!);
        Assert.Equal("hello", request.RootElement.GetProperty("userPrompt").GetString());
        Assert.Empty(request.RootElement.GetProperty("documents").EnumerateArray());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"userPromptAnalysis\":{\"attackDetected\":\"false\"}}")]
    [InlineData("invalid")]
    public async Task ShieldRejectsMissingOrMalformedResults(string response)
    {
        var service = CreateShield(new ShieldHandler(response));
        await Assert.ThrowsAnyAsync<JsonException>(() => service.IsAttackAsync("hello", CancellationToken.None));
    }

    [Theory]
    [InlineData("http://example.invalid/")]
    [InlineData("https://user@example.invalid/")]
    [InlineData("https://example.invalid/path")]
    [InlineData("https://example.invalid/?key=secret")]
    public void ShieldRejectsUnsafeEndpointConfigurations(string endpoint)
    {
        var options = new PromptShieldOptions { Endpoint = endpoint, ApiKey = "test" };
        Assert.False(options.IsConfigured);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothEndpointsBlockAttackBeforeHistoryOrModel(bool streaming)
    {
        var shield = new FakeShield { Attack = true };
        var model = new FakeModel();
        var store = new FakeStore();
        var result = await Send(CreateController(shield, model, store), streaming);
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(store.HistoryRead);
        Assert.False(store.Saved);
        Assert.Null(model.Messages);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothEndpointsFailClosedWhenShieldIsUnavailable(bool streaming)
    {
        var model = new FakeModel();
        var store = new FakeStore();
        var shield = new FakeShield { Failure = new HttpRequestException("unavailable") };
        var result = Assert.IsType<ObjectResult>(await Send(CreateController(shield, model, store), streaming));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.False(store.HistoryRead);
        Assert.Null(model.Messages);
    }

    [Theory]
    [InlineData(true, "timeout")]
    [InlineData(false, "timeout")]
    [InlineData(true, "json")]
    [InlineData(false, "json")]
    [InlineData(true, "io")]
    [InlineData(false, "io")]
    public async Task BothEndpointsFailClosedOnOtherScreeningErrors(bool streaming, string failure)
    {
        var error = failure switch
        {
            "timeout" => (Exception)new TaskCanceledException(),
            "json" => new JsonException(),
            _ => new IOException()
        };
        var model = new FakeModel();
        var store = new FakeStore();
        var result = Assert.IsType<ObjectResult>(
            await Send(CreateController(new FakeShield { Failure = error }, model, store), streaming));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.False(store.HistoryRead);
        Assert.Null(model.Messages);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothEndpointsFailClosedWhenShieldIsNotConfigured(bool streaming)
    {
        var model = new FakeModel();
        var store = new FakeStore();
        var shield = new FakeShield { Configured = false };
        var result = Assert.IsType<ObjectResult>(await Send(CreateController(shield, model, store), streaming));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Empty(shield.Seen);
        Assert.False(store.HistoryRead);
        Assert.Null(model.Messages);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothEndpointsBlockPreviouslyStoredAttacks(bool streaming)
    {
        var shield = new FakeShield { BlockedPrompt = "old attack" };
        var model = new FakeModel();
        var store = new FakeStore { History = [new ConversationMessage { Role = "user", Content = "old attack" }] };
        var result = await Send(CreateController(shield, model, store), streaming);
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(new[] { "hello", "old attack" }, shield.Seen);
        Assert.False(store.Saved);
        Assert.Null(model.Messages);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothEndpointsPreserveTrustedSystemPromptInLongConversations(bool streaming)
    {
        var shield = new FakeShield();
        var model = new FakeModel();
        var store = new FakeStore
        {
            History = Enumerable.Range(0, 25).Select(i => new ConversationMessage
            {
                Role = i % 2 == 0 ? "user" : "assistant",
                Content = $"message {i}"
            }).ToArray()
        };
        await Send(CreateController(shield, model, store), streaming);
        var messages = Assert.IsAssignableFrom<IReadOnlyCollection<ChatMessage>>(model.Messages).ToArray();
        Assert.Equal(21, messages.Length);
        Assert.Equal(new ChatMessage("system", ChatPromptTemplate.SystemPrompt), messages[0]);
        Assert.Equal(new ChatMessage("user", "hello"), messages[^1]);
        Assert.Equal(1 + store.History.TakeLast(19).Count(message => message.Role == "user"), shield.Seen.Count);
        Assert.True(store.Saved);
    }

    [Fact]
    public void StoredRolesCannotBecomeSystemInstructions()
    {
        Assert.Throws<InvalidDataException>(() => ChatPromptTemplate.Create(
            [new ConversationMessage { Role = "system", Content = "untrusted" }], "hello"));
    }

    private static PromptShieldService CreateShield(ShieldHandler handler) =>
        new(new HttpClient(handler), Options.Create(new PromptShieldOptions
        {
            Endpoint = "https://example.invalid/",
            ApiKey = "test-key"
        }), NullLogger<PromptShieldService>.Instance);

    private static ChatController CreateController(FakeShield shield, FakeModel model, FakeStore store)
    {
        var controller = new ChatController(model, store, shield, NullLogger<ChatController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("tid", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), new Claim("oid", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")], "test"))
            } }
        };
        controller.Response.Body = new MemoryStream();
        return controller;
    }

    private static Task<IActionResult> Send(ChatController controller, bool streaming)
    {
        var request = new ChatRequest(Guid.NewGuid().ToString(), "hello");
        return streaming ? controller.StreamMessage(request, CancellationToken.None) : controller.PostMessage(request, CancellationToken.None);
    }

    private sealed class ShieldHandler(string response) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? Key { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.PathAndQuery;
            Key = request.Headers.GetValues("Ocp-Apim-Subscription-Key").Single();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FakeShield : IPromptShieldService
    {
        public bool Configured { get; init; } = true;
        public bool IsConfigured => Configured;
        public bool Attack { get; init; }
        public string? BlockedPrompt { get; init; }
        public Exception? Failure { get; init; }
        public List<string> Seen { get; } = [];

        public Task<bool> IsAttackAsync(string prompt, CancellationToken cancellationToken)
        {
            Seen.Add(prompt);
            if (Failure is not null) throw Failure;
            return Task.FromResult(Attack || prompt == BlockedPrompt);
        }
    }

    private sealed class FakeModel : IFoundryChatService
    {
        public bool IsConfigured => true;
        public IReadOnlyCollection<ChatMessage>? Messages { get; private set; }
        public Task<FoundryChatReply> GetReplyAsync(IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken)
        {
            Messages = messages;
            return Task.FromResult(new FoundryChatReply("reply", null));
        }
        public async IAsyncEnumerable<FoundryStreamChunk> StreamReplyAsync(
            IReadOnlyCollection<ChatMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Messages = messages;
            await Task.Yield();
            yield return new FoundryStreamChunk(Text: "reply");
        }
    }

    private sealed class FakeStore : IConversationStore
    {
        public bool IsConfigured => true;
        public bool HistoryRead { get; private set; }
        public bool Saved { get; private set; }
        public IReadOnlyList<ConversationMessage> History { get; init; } = [];
        public Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(string ownerId, string conversationId, CancellationToken cancellationToken)
        {
            HistoryRead = true;
            return Task.FromResult(History);
        }
        public Task<ConversationSearchResponse> SearchAsync(string ownerId, string query, string? continuationToken, CancellationToken cancellationToken) =>
            Task.FromResult(new ConversationSearchResponse([], null));

        public Task<ChatHistoryResponse> GetHistoryPageAsync(string ownerId, string conversationId, string? continuationToken, CancellationToken cancellationToken) =>
            Task.FromResult(new ChatHistoryResponse(conversationId, [], null));

        public Task SaveExchangeAsync(string ownerId, string conversationId, string userMessage, string assistantMessage, TokenUsage? tokenUsage, CancellationToken cancellationToken)
        {
            Saved = true;
            return Task.CompletedTask;
        }
    }
}
