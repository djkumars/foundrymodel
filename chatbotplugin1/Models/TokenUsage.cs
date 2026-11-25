namespace foundry_chatbot_api.Models;

public sealed record TokenUsage(int PromptTokens, int CompletionTokens, int TotalTokens);
