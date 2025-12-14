using Microsoft.Azure.Cosmos;

namespace Ancilla.FunctionApp.Services;

/// <summary>
/// Service for managing chat history in Cosmos DB. Entries are partitioned by the AI's
/// phone number so that all history for a given AI is stored together.
/// </summary>
public class HistoryService(CosmosClient _cosmosClient)
{
    private const string DatabaseName = "ancilladb";
    private const string ContainerName = "history";

    private async Task<Container> GetContainerAsync()
    {
        var database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(DatabaseName);
        var containerResponse = await database.Database.CreateContainerIfNotExistsAsync(
            ContainerName,
            "/aiPhoneNumber");
        return containerResponse.Container;
    }

    public async Task CreateHistoryEntryAsync(string aiPhoneNumber, string userPhoneNumber, string content, MessageType messageType)
    {
        var container = await GetContainerAsync();

        var historyEntry = new
        {
            id = Guid.NewGuid(),
            aiPhoneNumber,
            userPhoneNumber,
            content,
            messageType,
            timestamp = DateTimeOffset.UtcNow
        };

        await container.CreateItemAsync(historyEntry, new PartitionKey(aiPhoneNumber));
        await ExpireAsync(container, aiPhoneNumber, userPhoneNumber);
    }

    public async Task<HistoryEntry[]> GetHistoryAsync(string aiPhoneNumber, string userPhoneNumber)
    {
        var container = await GetContainerAsync();

        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.aiPhoneNumber = @aiPhoneNumber AND c.userPhoneNumber = @userPhoneNumber ORDER BY c.timestamp ASC")
            .WithParameter("@aiPhoneNumber", aiPhoneNumber)
            .WithParameter("@userPhoneNumber", userPhoneNumber);

        var iterator = container.GetItemQueryIterator<HistoryEntry>(query);
        var entries = new List<HistoryEntry>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            entries.AddRange(response);
        }

        return [.. entries];
    }

    private static async Task ExpireAsync(Container container, string aiPhoneNumber, string userPhoneNumber)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.aiPhoneNumber = @aiPhoneNumber AND c.userPhoneNumber = @userPhoneNumber ORDER BY c.timestamp DESC")
            .WithParameter("@aiPhoneNumber", aiPhoneNumber)
            .WithParameter("@userPhoneNumber", userPhoneNumber);

        var iterator = container.GetItemQueryIterator<HistoryEntry>(query);
        var entries = new List<HistoryEntry>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            entries.AddRange(response);
        }

        // Delete entries beyond the 10 most recent.
        if (entries.Count > 10)
        {
            var entriesToDelete = entries.Skip(10);
            foreach (var entry in entriesToDelete)
                await container.DeleteItemAsync<HistoryEntry>(entry.Id.ToString(), new PartitionKey(aiPhoneNumber));
        }
    }
}

public class HistoryEntry
{
    public Guid Id { get; set; }
    public string UserPhoneNumber { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public MessageType MessageType { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

public enum MessageType
{
    User,
    Assistant
}