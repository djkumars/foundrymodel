namespace foundry_chatbot_api.Models;

public sealed record ChatRequest(string? ConversationId, string? Message);
