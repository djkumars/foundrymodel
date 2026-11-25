namespace foundry_chatbot_api.Models;

public sealed record FoundryChatReply(string? Text, TokenUsage? TokenUsage);

public sealed record FoundryStreamChunk(string? Text = null, TokenUsage? TokenUsage = null);
