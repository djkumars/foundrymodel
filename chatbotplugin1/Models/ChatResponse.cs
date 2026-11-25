namespace foundry_chatbot_api.Models;

public sealed record ChatResponse(string ConversationId, string Reply, TokenUsage? TokenUsage);
