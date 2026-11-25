using foundry_chatbot_api.Configuration;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

namespace foundry_chatbot_api.Services;

public sealed class CosmosModelSettingsStore : IModelSettingsStore, IDisposable
{
    private const string OwnerId = "system:model-settings";
    private const string DocumentId = "model-settings";
    private readonly CosmosDbOptions _options;
    private readonly CosmosClient? _client;

    public CosmosModelSettingsStore(IOptions<CosmosDbOptions> options)
    {
        _options = options.Value;
        if (_options.IsConfigured &&
            Uri.TryCreate(_options.Endpoint, UriKind.Absolute, out var endpoint) &&
            endpoint.Scheme == Uri.UriSchemeHttps)
        {
            _client = new CosmosClient(endpoint.ToString(), _options.Key, new CosmosClientOptions
            {
                SerializerOptions = new CosmosSerializationOptions
                {
                    PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
                }
            });
        }
    }

    public async Task<double?> GetTemperatureAsync(CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.id = @id").WithParameter("@id", DocumentId);
        var iterator = Container.GetItemQueryIterator<SettingsDocument>(query, requestOptions: new QueryRequestOptions
        {
            PartitionKey = new PartitionKey(OwnerId)
        });
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken);
            foreach (var document in page)
            {
                var temperature = document.Temperature;
                if (temperature is not null && (!double.IsFinite(temperature.Value) || temperature < 0 || temperature > 2))
                    throw new InvalidDataException("Stored model temperature is invalid.");
                return temperature;
            }
        }
        return null;
    }

    public async Task SetTemperatureAsync(double? temperature, CancellationToken cancellationToken)
    {
        if (temperature is not null && (!double.IsFinite(temperature.Value) || temperature < 0 || temperature > 2))
            throw new ArgumentOutOfRangeException(nameof(temperature));

        await Container.UpsertItemAsync(new SettingsDocument { Temperature = temperature },
            new PartitionKey(OwnerId), cancellationToken: cancellationToken);
    }

    public void Dispose() => _client?.Dispose();

    private Container Container => _client?.GetContainer(_options.DatabaseName, _options.UserContainerName)
        ?? throw new InvalidOperationException("Cosmos DB model settings storage is not configured.");

    private sealed class SettingsDocument
    {
        public string Id { get; set; } = DocumentId;
        public string OwnerId { get; set; } = CosmosModelSettingsStore.OwnerId;
        public string DocumentType { get; set; } = "model-settings";
        [Newtonsoft.Json.JsonProperty(Required = Newtonsoft.Json.Required.AllowNull)]
        public double? Temperature { get; set; }
    }
}
