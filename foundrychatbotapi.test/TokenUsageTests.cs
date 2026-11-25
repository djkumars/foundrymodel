using System.Net;
using System.Net.Http.Headers;
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
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace foundry_chatbot_api.Tests;

public class TokenUsageTests
{
    [Fact]
    public async Task StreamingRequestsAndParsesFinalUsageChunk()
    {
        var handler = new FakeFoundryHandler(
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}],\"usage\":null}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":3,\"total_tokens\":15}}\n\n" +
            "data: [DONE]\n\n");
        var service = CreateService(handler);
        var chunks = new List<FoundryStreamChunk>();

        await foreach (var chunk in service.StreamReplyAsync([new ChatMessage("user", "Hi")], CancellationToken.None))
            chunks.Add(chunk);

        using var request = JsonDocument.Parse(handler.RequestBody!);
        Assert.True(request.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(request.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal("Hello", chunks[0].Text);
        Assert.Equal(new TokenUsage(12, 3, 15), chunks[1].TokenUsage);
    }

    [Fact]
    public async Task MissingUsageRemainsUnavailable()
    {
        var handler = new FakeFoundryHandler(
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\n\ndata: [DONE]\n\n");
        var service = CreateService(handler);
        var chunks = new List<FoundryStreamChunk>();

        await foreach (var chunk in service.StreamReplyAsync([new ChatMessage("user", "Hi")], CancellationToken.None))
            chunks.Add(chunk);

        Assert.Single(chunks);
        Assert.Null(chunks[0].TokenUsage);
    }

    [Fact]
    public async Task NonStreamingReplyIncludesFoundryUsage()
    {
        var handler = new FakeFoundryHandler(
            "{\"choices\":[{\"message\":{\"content\":\"Hello\"}}]," +
            "\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":3,\"total_tokens\":15}}");
        var service = CreateService(handler);

        var reply = await service.GetReplyAsync([new ChatMessage("user", "Hi")], CancellationToken.None);

        Assert.Equal("Hello", reply.Text);
        Assert.Equal(new TokenUsage(12, 3, 15), reply.TokenUsage);
    }

    [Theory]
    [InlineData(true, 0.0)]
    [InlineData(false, 2.0)]
    [InlineData(true, null)]
    [InlineData(false, null)]
    public async Task FoundryRequestsUseSharedTemperatureOrOmitIt(bool streaming, double? temperature)
    {
        var handler = new FakeFoundryHandler(streaming
            ? "data: {\"choices\":[]}\n\ndata: [DONE]\n\n"
            : "{\"choices\":[{\"message\":{\"content\":\"Hello\"}}]}");
        var service = CreateService(handler, temperature);
        if (streaming)
        {
            await foreach (var _ in service.StreamReplyAsync([new ChatMessage("user", "Hi")], CancellationToken.None)) { }
        }
        else
        {
            await service.GetReplyAsync([new ChatMessage("user", "Hi")], CancellationToken.None);
        }

        using var request = JsonDocument.Parse(handler.RequestBody!);
        var present = request.RootElement.TryGetProperty("temperature", out var value);
        Assert.Equal(temperature.HasValue, present);
        if (temperature.HasValue) Assert.Equal(temperature.Value, value.GetDouble());
    }

    [Fact]
    public async Task SettingsFailurePreventsFoundryRequest()
    {
        var handler = new FakeFoundryHandler("{}");
        var service = new FoundryChatService(new HttpClient(handler), Options.Create(new FoundryOptions
        {
            Endpoint = "https://example.invalid", ApiKey = "test", Model = "test"
        }), new TestSettingsStore(null, fail: true), NullLogger<FoundryChatService>.Instance);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.GetReplyAsync([new ChatMessage("user", "Hi")], CancellationToken.None));
        Assert.Null(handler.RequestBody);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FoundryRequestKeepsSystemPromptWithLongHistory(bool streaming)
    {
        var response = streaming
            ? "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\n\ndata: [DONE]\n\n"
            : "{\"choices\":[{\"message\":{\"content\":\"Hello\"}}]}";
        var handler = new FakeFoundryHandler(response);
        var service = CreateService(handler);
        var history = Enumerable.Range(0, 25).Select(i =>
            new ConversationMessage { Role = "user", Content = $"message {i}" }).ToArray();
        var messages = ChatPromptTemplate.Create(history, "last user message");

        if (streaming)
        {
            await foreach (var _ in service.StreamReplyAsync(messages, CancellationToken.None)) { }
        }
        else
        {
            await service.GetReplyAsync(messages, CancellationToken.None);
        }

        using var request = JsonDocument.Parse(handler.RequestBody!);
        var sent = request.RootElement.GetProperty("messages");
        Assert.Equal(21, sent.GetArrayLength());
        Assert.Equal("system", sent[0].GetProperty("role").GetString());
        Assert.Equal(ChatPromptTemplate.SystemPrompt, sent[0].GetProperty("content").GetString());
        Assert.Equal("last user message", sent[20].GetProperty("content").GetString());
    }

    [Fact]
    public async Task InvalidUsageIsRejected()
    {
        var handler = new FakeFoundryHandler(
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":-1,\"completion_tokens\":3,\"total_tokens\":2}}\n\n");
        var service = CreateService(handler);

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(async () =>
        {
            await foreach (var _ in service.StreamReplyAsync([new ChatMessage("user", "Hi")], CancellationToken.None)) { }
        });
    }

    [Fact]
    public async Task CompletedUsageIsSavedAndReturnedInHistory()
    {
        var usage = new TokenUsage(12, 3, 15);
        var store = new FakeConversationStore();
        var controller = new ChatController(new FakeFoundryService(usage), store, new SafePromptShield(), NullLogger<ChatController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreateUser() }
            }
        };
        controller.Response.Body = new MemoryStream();
        var id = Guid.NewGuid();

        await controller.StreamMessage(new ChatRequest(id.ToString(), "Hi"), CancellationToken.None);
        Assert.Equal(usage, store.SavedUsage);
        controller.Response.Body.Position = 0;
        using var reader = new StreamReader(controller.Response.Body);
        var streamed = await reader.ReadToEndAsync();
        Assert.Contains("\"promptTokens\":12", streamed);
        Assert.Contains("\"completionTokens\":3", streamed);
        Assert.Contains("\"totalTokens\":15", streamed);
        var history = Assert.IsType<OkObjectResult>(await controller.GetHistory(id, null, CancellationToken.None));
        var response = Assert.IsType<ChatHistoryResponse>(history.Value);
        Assert.Equal(usage, Assert.Single(response.Messages).TokenUsage);
    }

    [Fact]
    public async Task NonStreamingReplySavesAndReturnsUsage()
    {
        var usage = new TokenUsage(12, 3, 15);
        var store = new FakeConversationStore();
        var controller = new ChatController(new FakeFoundryService(usage), store, new SafePromptShield(), NullLogger<ChatController>.Instance);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = CreateUser() } };

        var result = Assert.IsType<OkObjectResult>(await controller.PostMessage(
            new ChatRequest(Guid.NewGuid().ToString(), "Hi"), CancellationToken.None));

        Assert.Equal(usage, store.SavedUsage);
        Assert.Equal(usage, Assert.IsType<ChatResponse>(result.Value).TokenUsage);
    }

    [Fact]
    public void StoredUsageAndOlderDocumentsDeserialize()
    {
        var settings = new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() };
        var saved = new ConversationMessage { Role = "assistant", TokenUsage = new TokenUsage(12, 3, 15) };
        var json = JsonConvert.SerializeObject(saved, settings);
        Assert.Equal(saved.TokenUsage, JsonConvert.DeserializeObject<ConversationMessage>(json, settings)!.TokenUsage);
        Assert.Null(JsonConvert.DeserializeObject<ConversationMessage>("{\"role\":\"assistant\"}", settings)!.TokenUsage);
    }

    private static FoundryChatService CreateService(FakeFoundryHandler handler, double? temperature = null) =>
        new(new HttpClient(handler), Options.Create(new FoundryOptions
        {
            Endpoint = "https://example.invalid/openai/v1/chat/completions",
            ApiKey = "test",
            Model = "test"
        }), new TestSettingsStore(temperature), NullLogger<FoundryChatService>.Instance);

    private sealed class TestSettingsStore(double? temperature, bool fail = false) : IModelSettingsStore
    {
        public Task<double?> GetTemperatureAsync(CancellationToken cancellationToken) =>
            fail ? throw new InvalidDataException("Invalid settings") : Task.FromResult(temperature);

        public Task SetTemperatureAsync(double? value, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private static ClaimsPrincipal CreateUser() => new(new ClaimsIdentity(
        [new Claim("tid", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), new Claim("oid", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")], "test"));

    private sealed class FakeFoundryHandler(string data) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var content = new StringContent(data, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    private sealed class FakeFoundryService(TokenUsage usage) : IFoundryChatService
    {
        public bool IsConfigured => true;
        public Task<FoundryChatReply> GetReplyAsync(IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken) =>
            Task.FromResult(new FoundryChatReply("Hello", usage));

        public async IAsyncEnumerable<FoundryStreamChunk> StreamReplyAsync(
            IReadOnlyCollection<ChatMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new FoundryStreamChunk(Text: "Hello");
            yield return new FoundryStreamChunk(TokenUsage: usage);
        }
    }

    private sealed class FakeConversationStore : IConversationStore
    {
        public bool IsConfigured => true;
        public TokenUsage? SavedUsage { get; private set; }

        public Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(string ownerId, string conversationId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ConversationMessage>>(
                [new ConversationMessage { Role = "assistant", Content = "Hello", TokenUsage = SavedUsage }]);

        public Task<ConversationSearchResponse> SearchAsync(string ownerId, string query, string? continuationToken, CancellationToken cancellationToken) =>
            Task.FromResult(new ConversationSearchResponse([], null));

        public async Task<ChatHistoryResponse> GetHistoryPageAsync(string ownerId, string conversationId, string? continuationToken, CancellationToken cancellationToken)
        {
            var messages = await GetMessagesAsync(ownerId, conversationId, cancellationToken);
            return new ChatHistoryResponse(conversationId,
                messages.Select(m => new ChatHistoryMessage(m.Id, m.Role, m.Content, m.CreatedAt, m.TokenUsage)).ToArray());
        }

        public Task SaveExchangeAsync(string ownerId, string conversationId, string userMessage, string assistantMessage, TokenUsage? tokenUsage, CancellationToken cancellationToken)
        {
            SavedUsage = tokenUsage;
            return Task.CompletedTask;
        }

    }

    private sealed class SafePromptShield : IPromptShieldService
    {
        public bool IsConfigured => true;
        public Task<bool> IsAttackAsync(string prompt, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
