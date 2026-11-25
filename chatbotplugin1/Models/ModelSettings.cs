using System.ComponentModel.DataAnnotations;

namespace foundry_chatbot_api.Models;

/// <summary>Updates the shared temperature, or uses the model default when null.</summary>
public sealed record UpdateModelSettingsRequest
{
    [Range(0, 2)]
    public required double? Temperature { get; init; }
}

/// <summary>Shared model temperature; null means the model default is used.</summary>
public sealed record ModelSettingsResponse(double? Temperature);
