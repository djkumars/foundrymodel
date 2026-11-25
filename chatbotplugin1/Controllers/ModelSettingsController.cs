using foundry_chatbot_api.Models;
using foundry_chatbot_api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;

namespace foundry_chatbot_api.Controllers;

[ApiController]
[Authorize(Policy = "ChatAccess")]
[Authorize(Policy = "AdminAccess")]
[Route("api/admin/model-settings")]
public sealed class ModelSettingsController(
    IModelSettingsStore store,
    ILogger<ModelSettingsController> logger) : ControllerBase
{
    /// <summary>Reads the shared model settings.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ModelSettingsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(new ModelSettingsResponse(await store.GetTemperatureAsync(cancellationToken)));
        }
        catch (Exception exception) when (exception is CosmosException or InvalidOperationException or InvalidDataException)
        {
            logger.LogError(exception, "Reading model settings failed.");
            return StatusCode(503, new ErrorResponse("Model settings are unavailable. Please try again."));
        }
    }

    /// <summary>Updates the shared model temperature.</summary>
    [HttpPut]
    [ProducesResponseType(typeof(ModelSettingsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Put([FromBody] UpdateModelSettingsRequest request, CancellationToken cancellationToken)
    {
        if (request.Temperature is double value && (!double.IsFinite(value) || value < 0 || value > 2))
            return BadRequest(new ErrorResponse("Temperature must be between 0 and 2."));

        try
        {
            await store.SetTemperatureAsync(request.Temperature, cancellationToken);
            return Ok(new ModelSettingsResponse(request.Temperature));
        }
        catch (Exception exception) when (exception is CosmosException or InvalidOperationException)
        {
            logger.LogError(exception, "Saving model settings failed.");
            return StatusCode(503, new ErrorResponse("Model settings could not be saved. Please try again."));
        }
    }
}
