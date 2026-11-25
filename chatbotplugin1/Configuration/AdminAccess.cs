using System.Security.Claims;

namespace foundry_chatbot_api.Configuration;

public static class AdminAccess
{
    public static bool IsAllowed(ClaimsPrincipal user, IEnumerable<string> allowedObjectIds) =>
        Guid.TryParse(user.FindFirst("oid")?.Value, out var objectId) &&
        allowedObjectIds.Any(value => Guid.TryParse(value, out var allowed) && allowed == objectId);
}
