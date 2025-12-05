using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;

namespace Ancilla.FunctionApp;

public class HistoryService
{
    private readonly CosmosClient _cosmosClient;
    private const string DatabaseName = "ancilladb";
    private const string ContainerName = "history";
    private bool _initialized = false;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public HistoryService(CosmosClient cosmosClient)
    {
        _cosmosClient = cosmosClient;
    }

    private async Task<Microsoft.Azure.Cosmos.Container> GetContainerAsync()
    {
        if (!_initialized)
        {
            await _initLock.WaitAsync();
            try
            {
                if (!_initialized)
                {
                    var database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(DatabaseName);
                    await database.Database.CreateContainerIfNotExistsAsync(ContainerName, "/userPhoneNumber");
                    _initialized = true;
                }
            }
            finally
            {
                _initLock.Release();
            }
        }

        return _cosmosClient.GetDatabase(DatabaseName).GetContainer(ContainerName);
    }

    public async Task CreateHistoryEntryAsync(string userPhoneNumber, string content, MessageType messageType)
    {
        var container = await GetContainerAsync();
        
        var historyEntry = new HistoryEntry
        {
            id = Guid.NewGuid(),
            userPhoneNumber = userPhoneNumber,
            content = content,
            messageType = messageType,
            timestamp = DateTimeOffset.UtcNow
        };

        await container.CreateItemAsync(historyEntry, new PartitionKey(userPhoneNumber));
        await ExpireAsync(container, userPhoneNumber);
    }

    public async Task<List<HistoryEntry>> GetHistoryAsync(string userPhoneNumber)
    {
        var container = await GetContainerAsync();
        
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.userPhoneNumber = @userPhoneNumber ORDER BY c.timestamp ASC")
            .WithParameter("@userPhoneNumber", userPhoneNumber);

        var iterator = container.GetItemQueryIterator<HistoryEntry>(query);
        var entries = new List<HistoryEntry>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            entries.AddRange(response);
        }

        return entries;
    }

    private async Task ExpireAsync(Microsoft.Azure.Cosmos.Container container, string userPhoneNumber)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.userPhoneNumber = @userPhoneNumber ORDER BY c.timestamp DESC")
            .WithParameter("@userPhoneNumber", userPhoneNumber);

        var iterator = container.GetItemQueryIterator<HistoryEntry>(query);
        var entries = new List<HistoryEntry>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            entries.AddRange(response);
        }

        // Delete entries beyond the 10 most recent
        if (entries.Count > 10)
        {
            var entriesToDelete = entries.Skip(10);
            foreach (var entry in entriesToDelete)
            {
                await container.DeleteItemAsync<HistoryEntry>(entry.id.ToString(), new PartitionKey(userPhoneNumber));
            }
        }
    }
}

public class HistoryEntry
{
    public Guid id { get; set; }
    
    [JsonPropertyName("userPhoneNumber")]
    public string userPhoneNumber { get; set; } = string.Empty;
    
    [JsonPropertyName("content")]
    public string content { get; set; } = string.Empty;
    
    [JsonPropertyName("messageType")]
    public MessageType messageType { get; set; }
    
    [JsonPropertyName("timestamp")]
    public DateTimeOffset timestamp { get; set; }
}

public enum MessageType
{
    User,
    Assistant
}
