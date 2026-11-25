namespace foundry_chatbot_api.Services;

public interface IModelSettingsStore
{
    Task<double?> GetTemperatureAsync(CancellationToken cancellationToken);
    Task SetTemperatureAsync(double? temperature, CancellationToken cancellationToken);
}
