using Microsoft.Azure.Cosmos;

namespace Ancilla.FunctionApp.Services;

public interface IKnowledgeService
{
    Task SaveKnowledgeAsync(string agentPhoneNumber, string userPhoneNumber, string content);
    Task<KnowledgeEntry[]> GetKnowledgeAsync(string agentPhoneNumber);
    Task DeleteKnowledgeAsync(Guid id, string agentPhoneNumber);
}

/// <summary>
/// Service for managing knowledge entries in Cosmos DB. Entries are partitioned by the AI's
/// phone number so that all knowledge for a given AI are stored together.
/// </summary>
public class KnowledgeService(CosmosClient _cosmosClient) : IKnowledgeService
{
    private const string DatabaseName = "ancilladb";
    private const string ContainerName = "knowledge";

    private async Task<Container> GetContainerAsync()
    {
        var database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(DatabaseName);
        var containerResponse = await database.Database.CreateContainerIfNotExistsAsync(
            ContainerName,
            "/agentPhoneNumber");
        return containerResponse.Container;
    }

    public async Task SaveKnowledgeAsync(string agentPhoneNumber, string userPhoneNumber, string content)
    {
        ArgumentNullException.ThrowIfNull(agentPhoneNumber);
        ArgumentNullException.ThrowIfNull(userPhoneNumber);
        ArgumentNullException.ThrowIfNull(content);

        var knowledge = new
        {
            id = Guid.NewGuid().ToString(),
            content,
            userPhoneNumber,
            agentPhoneNumber,
            created = DateTimeOffset.Now,
            deleted = (DateTimeOffset?)null
        };

        var container = await GetContainerAsync();
        await container.CreateItemAsync(knowledge, new PartitionKey(knowledge.agentPhoneNumber));
    }

    public async Task<KnowledgeEntry[]> GetKnowledgeAsync(string agentPhoneNumber)
    {
        ArgumentNullException.ThrowIfNull(agentPhoneNumber);

        var container = await GetContainerAsync();

        var query = new QueryDefinition("SELECT * FROM c WHERE c.agentPhoneNumber = @phoneNumber")
                        .WithParameter("@phoneNumber", agentPhoneNumber);
        var iterator = container.GetItemQueryIterator<KnowledgeEntry>(query);
        var entries = new List<KnowledgeEntry>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            entries.AddRange(response);
        }
        return [.. entries];
    }

    public async Task DeleteKnowledgeAsync(Guid id, string agentPhoneNumber)
    {
        ArgumentNullException.ThrowIfNull(agentPhoneNumber);

        var container = await GetContainerAsync();

        var response = await container.ReadItemAsync<dynamic>(id.ToString(), new PartitionKey(agentPhoneNumber));
        var knowledge = response.Resource;
        knowledge.deleted = DateTimeOffset.Now;
        await container.ReplaceItemAsync(knowledge, id.ToString(), new PartitionKey(agentPhoneNumber));
    }
}

public class KnowledgeEntry
{
    public Guid Id { get; set; }
    public required string Content { get; set; }
    public required string UserPhoneNumber { get; set; }
    public required DateTimeOffset Created { get; set; }
    public required DateTimeOffset? Deleted { get; set; }
}