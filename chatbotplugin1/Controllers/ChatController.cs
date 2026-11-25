using System.Text;
using System.Text.Json;
using foundry_chatbot_api.Models;
using foundry_chatbot_api.Prompts;
using foundry_chatbot_api.Services;
using Microsoft.Azure.Cosmos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace foundry_chatbot_api.Controllers;

[ApiController]
[Authorize(Policy = "ChatAccess")]
[Route("api/chat")]
public sealed class ChatController(
    IFoundryChatService foundryChatService,
    IConversationStore conversationStore,
    IPromptShieldService promptShieldService,
    ILogger<ChatController> logger) : ControllerBase
{
    [HttpGet("{conversationId:guid}")]
    public async Task<IActionResult> GetHistory(
        Guid conversationId,
        [FromQuery] string? continuationToken,
        CancellationToken cancellationToken)
    {
        if (!TryGetOwnerId(out var ownerId)) return Unauthorized();
        if (!conversationStore.IsConfigured)
        {
            return CosmosNotConfigured();
        }
        if (continuationToken?.Length > 16384)
        {
            return BadRequest(new ErrorResponse("The history continuation token is too long."));
        }

        try
        {
            return Ok(await conversationStore.GetHistoryPageAsync(
                ownerId, conversationId.ToString("D"), continuationToken, cancellationToken));
        }
        catch (CosmosException exception) when (exception.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            logger.LogWarning("Conversation history received an invalid continuation token.");
            return BadRequest(new ErrorResponse("The history continuation token is invalid."));
        }
        catch (CosmosException exception)
        {
            logger.LogError(exception, "Reading conversation history from Cosmos DB failed.");
            return CosmosUnavailable();
        }
    }

    /// <summary>Lists conversations or searches message content for the signed-in user.</summary>
    [HttpGet("conversations")]
    [EnableRateLimiting("chat")]
    [ProducesResponseType(typeof(ConversationSearchResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> SearchConversations(
        [FromQuery] string? query,
        [FromQuery] string? continuationToken,
        CancellationToken cancellationToken)
    {
        if (!TryGetOwnerId(out var ownerId)) return Unauthorized();
        if (!conversationStore.IsConfigured) return CosmosNotConfigured();
        var term = query?.Trim() ?? string.Empty;
        if (term.Length > 200 || continuationToken?.Length > 16384)
        {
            return BadRequest(new ErrorResponse("The search query or continuation token is too long."));
        }
        try
        {
            return Ok(await conversationStore.SearchAsync(ownerId, term, continuationToken, cancellationToken));
        }
        catch (CosmosException exception) when (exception.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            logger.LogWarning("Conversation search received an invalid continuation token.");
            return BadRequest(new ErrorResponse("The search continuation token is invalid."));
        }
        catch (CosmosException exception)
        {
            logger.LogError(exception, "Searching conversation history failed.");
            return CosmosUnavailable();
        }
    }

    [HttpPost]
    [EnableRateLimiting("chat")]
    [RequestSizeLimit(32 * 1024)]
    public async Task<IActionResult> PostMessage(
        [FromBody] ChatRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryGetOwnerId(out var ownerId)) return Unauthorized();
        if (!foundryChatService.IsConfigured)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new ErrorResponse("The Foundry model is not configured on this server."));
        }

        if (!conversationStore.IsConfigured)
        {
            return CosmosNotConfigured();
        }

        if (!IsValidRequest(request))
        {
            return BadRequest(new ErrorResponse("Send a valid conversation ID and a message of up to 4,000 characters."));
        }

        var screeningError = await ScreenPromptAsync(request!.Message!.Trim(), cancellationToken);
        if (screeningError is not null) return screeningError;

        try
        {
            var conversationId = Guid.Parse(request.ConversationId!).ToString("D");
            var history = await conversationStore.GetMessagesAsync(ownerId, conversationId, cancellationToken);
            var historyScreeningError = await ScreenHistoryAsync(history, cancellationToken);
            if (historyScreeningError is not null) return historyScreeningError;
            var modelMessages = ChatPromptTemplate.Create(history, request.Message!.Trim());

            var reply = await foundryChatService.GetReplyAsync(
                modelMessages,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(reply.Text))
            {
                return StatusCode(
                    StatusCodes.Status502BadGateway,
                    new ErrorResponse("The AI service returned an empty reply. Please try again."));
            }

            await conversationStore.SaveExchangeAsync(
                ownerId,
                conversationId,
                request.Message.Trim(),
                reply.Text,
                reply.TokenUsage,
                cancellationToken);

            return Ok(new ChatResponse(conversationId, reply.Text, reply.TokenUsage));
        }
        catch (CosmosException exception)
        {
            logger.LogError(exception, "Conversation persistence in Cosmos DB failed.");
            return CosmosUnavailable();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError("Foundry chat request timed out.");
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new ErrorResponse("The AI service is temporarily unavailable. Please try again."));
        }
        catch (HttpRequestException exception)
        {
            logger.LogError(exception, "Foundry chat request failed.");
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new ErrorResponse("The AI service could not complete your request. Please try again."));
        }
        catch (InvalidOperationException exception)
        {
            logger.LogError(exception, "Foundry chat endpoint configuration is invalid.");
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new ErrorResponse("The Foundry chat endpoint must be a valid HTTPS URL."));
        }
        catch (JsonException exception)
        {
            logger.LogError(exception, "Foundry returned an invalid chat response.");
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new ErrorResponse("The AI service returned an invalid reply. Please try again."));
        }
        catch (InvalidDataException exception)
        {
            logger.LogError(exception, "Conversation history contains invalid data.");
            return CosmosUnavailable();
        }
    }

    [HttpPost("stream")]
    [EnableRateLimiting("chat")]
    [RequestSizeLimit(32 * 1024)]
    public async Task<IActionResult> StreamMessage(
        [FromBody] ChatRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryGetOwnerId(out var ownerId)) return Unauthorized();
        if (!foundryChatService.IsConfigured)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new ErrorResponse("The Foundry model is not configured on this server."));
        }

        if (!conversationStore.IsConfigured)
        {
            return CosmosNotConfigured();
        }

        if (!IsValidRequest(request))
        {
            return BadRequest(new ErrorResponse("Send a valid conversation ID and a message of up to 4,000 characters."));
        }

        var screeningError = await ScreenPromptAsync(request!.Message!.Trim(), cancellationToken);
        if (screeningError is not null) return screeningError;

        try
        {
            var conversationId = Guid.Parse(request.ConversationId!).ToString("D");
            var message = request.Message!.Trim();
            var history = await conversationStore.GetMessagesAsync(ownerId, conversationId, cancellationToken);
            var historyScreeningError = await ScreenHistoryAsync(history, cancellationToken);
            if (historyScreeningError is not null) return historyScreeningError;
            var modelMessages = ChatPromptTemplate.Create(history, message);
            var reply = new StringBuilder();
            TokenUsage? tokenUsage = null;

            await foreach (var chunk in foundryChatService.StreamReplyAsync(modelMessages, cancellationToken))
            {
                if (chunk.Text is not null)
                {
                    reply.Append(chunk.Text);
                    await WriteStreamEventAsync("delta", new { text = chunk.Text }, cancellationToken);
                }
                if (chunk.TokenUsage is not null) tokenUsage = chunk.TokenUsage;
            }

            if (string.IsNullOrWhiteSpace(reply.ToString()))
            {
                return await StreamErrorAsync(
                    StatusCodes.Status502BadGateway,
                    "The AI service returned an empty reply. Please try again.",
                    cancellationToken);
            }

            await conversationStore.SaveExchangeAsync(ownerId, conversationId, message, reply.ToString(), tokenUsage, cancellationToken);
            await WriteStreamEventAsync("done", new { conversationId, tokenUsage }, cancellationToken);
            return new EmptyResult();
        }
        catch (CosmosException exception)
        {
            logger.LogError(exception, "Conversation persistence in Cosmos DB failed.");
            return await StreamErrorAsync(
                StatusCodes.Status503ServiceUnavailable,
                "Conversation history is temporarily unavailable. Please try again.",
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError("Foundry chat request timed out.");
            return await StreamErrorAsync(
                StatusCodes.Status502BadGateway,
                "The AI service is temporarily unavailable. Please try again.",
                cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            logger.LogError(exception, "Foundry chat request failed.");
            return await StreamErrorAsync(
                StatusCodes.Status502BadGateway,
                "The AI service could not complete your request. Please try again.",
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            logger.LogError(exception, "Foundry chat endpoint configuration is invalid.");
            return await StreamErrorAsync(
                StatusCodes.Status503ServiceUnavailable,
                "The Foundry chat endpoint must be a valid HTTPS URL.",
                cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException)
        {
            logger.LogError(exception, "Foundry streaming response failed.");
            return await StreamErrorAsync(
                StatusCodes.Status502BadGateway,
                "The AI service returned an incomplete or invalid reply. Please try again.",
                cancellationToken);
        }
    }

    private async Task<IActionResult?> ScreenHistoryAsync(
        IReadOnlyList<ConversationMessage> history,
        CancellationToken cancellationToken)
    {
        foreach (var message in history.TakeLast(19).Where(message => message.Role == "user"))
        {
            var error = await ScreenPromptAsync(message.Content, cancellationToken);
            if (error is not null) return error;
        }
        return null;
    }

    private async Task<IActionResult?> ScreenPromptAsync(string prompt, CancellationToken cancellationToken)
    {
        if (!promptShieldService.IsConfigured)
        {
            logger.LogWarning("Prompt Shields is not configured.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new ErrorResponse("Prompt security screening is unavailable. Please try again later."));
        }

        try
        {
            if (await promptShieldService.IsAttackAsync(prompt, cancellationToken))
            {
                logger.LogWarning("Prompt Shields detected a prompt attack.");
                return BadRequest(new ErrorResponse("A message in this conversation was blocked by prompt security screening. Please rephrase it or start a new conversation."));
            }
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError("Prompt Shields request timed out.");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or InvalidOperationException)
        {
            logger.LogError(exception, "Prompt Shields screening failed.");
        }

        return StatusCode(StatusCodes.Status503ServiceUnavailable,
            new ErrorResponse("Prompt security screening is unavailable. Please try again later."));
    }

    private async Task<IActionResult> StreamErrorAsync(int statusCode, string message, CancellationToken cancellationToken)
    {
        if (!Response.HasStarted)
        {
            return StatusCode(statusCode, new ErrorResponse(message));
        }

        await WriteStreamEventAsync("error", new ErrorResponse(message), cancellationToken);
        return new EmptyResult();
    }

    private async Task WriteStreamEventAsync(string eventName, object data, CancellationToken cancellationToken)
    {
        if (!Response.HasStarted)
        {
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";
            Response.Headers["X-Accel-Buffering"] = "no";
        }

        await Response.WriteAsync(
            $"event: {eventName}\ndata: {JsonSerializer.Serialize(data, JsonSerializerOptions.Web)}\n\n",
            cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }

    private static bool IsValidRequest(ChatRequest? request) =>
        request is not null &&
        Guid.TryParse(request.ConversationId, out _) &&
        !string.IsNullOrWhiteSpace(request.Message) &&
        request.Message.Length <= 4000;

    private bool TryGetOwnerId(out string ownerId)
    {
        ownerId = string.Empty;
        if (User.Identity?.IsAuthenticated != true ||
            !Guid.TryParse(User.FindFirst("tid")?.Value, out var tenantId) ||
            !Guid.TryParse(User.FindFirst("oid")?.Value, out var objectId))
        {
            return false;
        }
        ownerId = $"{tenantId:D}:{objectId:D}";
        return true;
    }

    private IActionResult CosmosNotConfigured() =>
        StatusCode(
            StatusCodes.Status503ServiceUnavailable,
            new ErrorResponse("Cosmos DB conversation storage is not configured on this server."));

    private IActionResult CosmosUnavailable() =>
        StatusCode(
            StatusCodes.Status503ServiceUnavailable,
            new ErrorResponse("Conversation history is temporarily unavailable. Please try again."));
}
