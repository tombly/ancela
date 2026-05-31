using Microsoft.Azure.Cosmos;

namespace Ancela.Agent.Services;

public interface IHistoryService
{
    Task CreateHistoryEntryAsync(string agentPhoneNumber, string userPhoneNumber, string content, MessageType messageType);
    Task<HistoryEntry[]> GetHistoryAsync(string agentPhoneNumber, string userPhoneNumber);
}

/// <summary>
/// Service for managing chat history in Cosmos DB. Entries are partitioned by the AI's
/// phone number so that all history for a given AI is stored together.
/// </summary>
public class HistoryService(CosmosClient _cosmosClient) : IHistoryService
{
    private const string DatabaseName = "anceladb";
    private const string ContainerName = "history";

    // Number of most-recent entries (interleaved user + agent) retained and fed to the
    // model per user. Recency-by-count, not by time: conversations can be highly async,
    // so a thread resumed days later still carries its last turns. GetHistoryAsync and
    // ExpireAsync must use the same bound or trimmed entries could leak into the model.
    private const int MaxHistoryEntries = 20;

    private async Task<Container> GetContainerAsync()
    {
        var database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(DatabaseName);
        var containerResponse = await database.Database.CreateContainerIfNotExistsAsync(
            ContainerName,
            "/agentPhoneNumber");
        return containerResponse.Container;
    }

    public async Task CreateHistoryEntryAsync(string agentPhoneNumber, string userPhoneNumber, string content, MessageType messageType)
    {
        var container = await GetContainerAsync();

        var historyEntry = new
        {
            id = Guid.NewGuid(),
            agentPhoneNumber,
            userPhoneNumber,
            content,
            messageType,
            timestamp = DateTimeOffset.UtcNow
        };

        await container.CreateItemAsync(historyEntry, new PartitionKey(agentPhoneNumber));
        await ExpireAsync(container, agentPhoneNumber, userPhoneNumber);
    }

    public async Task<HistoryEntry[]> GetHistoryAsync(string agentPhoneNumber, string userPhoneNumber)
    {
        var container = await GetContainerAsync();

        // Bound the result to the most recent entries regardless of whether ExpireAsync has
        // kept up; otherwise a lagging/failed trim would feed unbounded history to the model.
        var query = new QueryDefinition(
            $"SELECT * FROM c WHERE c.agentPhoneNumber = @agentPhoneNumber AND c.userPhoneNumber = @userPhoneNumber ORDER BY c.timestamp DESC OFFSET 0 LIMIT {MaxHistoryEntries}")
            .WithParameter("@agentPhoneNumber", agentPhoneNumber)
            .WithParameter("@userPhoneNumber", userPhoneNumber);

        var iterator = container.GetItemQueryIterator<HistoryEntry>(query);
        var entries = new List<HistoryEntry>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            entries.AddRange(response);
        }

        // Query returns newest-first; chat history is consumed oldest-first.
        entries.Reverse();
        return [.. entries];
    }

    private static async Task ExpireAsync(Container container, string agentPhoneNumber, string userPhoneNumber)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.agentPhoneNumber = @agentPhoneNumber AND c.userPhoneNumber = @userPhoneNumber ORDER BY c.timestamp DESC")
            .WithParameter("@agentPhoneNumber", agentPhoneNumber)
            .WithParameter("@userPhoneNumber", userPhoneNumber);

        var iterator = container.GetItemQueryIterator<HistoryEntry>(query);
        var entries = new List<HistoryEntry>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            entries.AddRange(response);
        }

        // Delete entries beyond the most recent.
        if (entries.Count > MaxHistoryEntries)
        {
            var entriesToDelete = entries.Skip(MaxHistoryEntries);
            foreach (var entry in entriesToDelete)
            {
                try
                {
                    await container.DeleteItemAsync<HistoryEntry>(entry.Id.ToString(), new PartitionKey(agentPhoneNumber));
                }
                catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // A concurrent write for the same user may have already trimmed this entry.
                }
            }
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
    Agent
}
