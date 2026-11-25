namespace foundry_chatbot_api.Configuration;

public static class ChatRateLimitPartition
{
    public static string GetKey(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true &&
            Guid.TryParse(context.User.FindFirst("tid")?.Value, out var tenantId) &&
            Guid.TryParse(context.User.FindFirst("oid")?.Value, out var objectId))
        {
            return $"user:{tenantId:D}:{objectId:D}";
        }

        return $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }
}
