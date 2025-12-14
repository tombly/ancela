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
            "/aiPhoneNumber");
        return containerResponse.Container;
    }

    public async Task CreateSessionAsync(string aiPhoneNumber, string userPhoneNumber)
    {
        var container = await GetContainerAsync();

        var sessionEntry = new
        {
            id = Guid.NewGuid(),
            aiPhoneNumber,
            userPhoneNumber
        };

        await container.CreateItemAsync(sessionEntry, new PartitionKey(aiPhoneNumber));
    }

    public async Task<SessionEntry?> GetSessionAsync(string aiPhoneNumber, string userPhoneNumber)
    {
        var container = await GetContainerAsync();

        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.aiPhoneNumber = @aiPhoneNumber AND c.userPhoneNumber = @userPhoneNumber")
            .WithParameter("@aiPhoneNumber", aiPhoneNumber)
            .WithParameter("@userPhoneNumber", userPhoneNumber);

        var iterator = container.GetItemQueryIterator<SessionEntry>(query);

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            if (response.Any())
            {
                return response.First();
            }
        }

        return null;
    }

    public async Task<SessionEntry[]> GetAllSessionsAsync(string aiPhoneNumber)
    {
        var container = await GetContainerAsync();

        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.aiPhoneNumber = @aiPhoneNumber")
            .WithParameter("@aiPhoneNumber", aiPhoneNumber);

        var iterator = container.GetItemQueryIterator<SessionEntry>(query);
        var entries = new List<SessionEntry>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            entries.AddRange(response);
        }

        return [.. entries];
    }

    public async Task DeleteSessionAsync(string aiPhoneNumber, string userPhoneNumber)
    {
        var container = await GetContainerAsync();
        var session = await GetSessionAsync(aiPhoneNumber, userPhoneNumber);

        if (session != null)
            await container.DeleteItemAsync<SessionEntry>(session.Id.ToString(), new PartitionKey(aiPhoneNumber));
    }
}

public class SessionEntry
{
    public Guid Id { get; set; }
    public string AiPhoneNumber { get; set; } = string.Empty;
    public string UserPhoneNumber { get; set; } = string.Empty;
}