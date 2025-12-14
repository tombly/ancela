using Microsoft.Azure.Cosmos;

namespace Ancilla.FunctionApp.Services;

/// <summary>
/// Service for managing sessions in Cosmos DB. Entries are partitioned by the AI's
/// phone number so that all sessions for a given AI are stored together.
/// </summary>
public class SessionService(CosmosClient _cosmosClient)
{
    private const string DatabaseName = "ancilladb";
    private const string ContainerName = "sessions";

    private async Task<Container> GetContainerAsync()
    {
        var database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(DatabaseName);
        var containerResponse = await database.Database.CreateContainerIfNotExistsAsync(
            ContainerName,
            "/agentPhoneNumber");
        return containerResponse.Container;
    }

    public async Task CreateSessionAsync(string agentPhoneNumber, string userPhoneNumber, string timeZone = "Pacific Standard Time")
    {
        var container = await GetContainerAsync();

        var sessionEntry = new
        {
            id = Guid.NewGuid(),
            agentPhoneNumber,
            userPhoneNumber,
            created = DateTimeOffset.UtcNow,
            timeZone
        };

        await container.CreateItemAsync(sessionEntry, new PartitionKey(agentPhoneNumber));
    }

    public async Task<SessionEntry?> GetSessionAsync(string agentPhoneNumber, string userPhoneNumber)
    {
        var container = await GetContainerAsync();

        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.agentPhoneNumber = @agentPhoneNumber AND c.userPhoneNumber = @userPhoneNumber")
            .WithParameter("@agentPhoneNumber", agentPhoneNumber)
            .WithParameter("@userPhoneNumber", userPhoneNumber);

        var iterator = container.GetItemQueryIterator<SessionEntry>(query);

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            if (response.Any())
                return response.First();
        }

        return null;
    }

    public async Task<SessionEntry[]> GetAllSessionsAsync(string agentPhoneNumber)
    {
        var container = await GetContainerAsync();

        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.agentPhoneNumber = @agentPhoneNumber")
            .WithParameter("@agentPhoneNumber", agentPhoneNumber);

        var iterator = container.GetItemQueryIterator<SessionEntry>(query);
        var entries = new List<SessionEntry>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            entries.AddRange(response);
        }

        return [.. entries];
    }

    public async Task DeleteSessionAsync(string agentPhoneNumber, string userPhoneNumber)
    {
        var container = await GetContainerAsync();
        var session = await GetSessionAsync(agentPhoneNumber, userPhoneNumber);

        if (session != null)
            await container.DeleteItemAsync<SessionEntry>(session.Id.ToString(), new PartitionKey(agentPhoneNumber));
    }
}

public class SessionEntry
{
    public Guid Id { get; set; }
    public string AgentPhoneNumber { get; set; } = string.Empty;
    public string UserPhoneNumber { get; set; } = string.Empty;
    public DateTimeOffset Created { get; set; }
    public string TimeZone { get; set; } = "Pacific Standard Time";
}