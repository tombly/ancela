using Ancela.Agent.SemanticKernel.Plugins.MemoryPlugin.Models;
using Microsoft.Azure.Cosmos;

namespace Ancela.Agent.SemanticKernel.Plugins.MemoryPlugin;

public interface IMemoryClient
{
    Task SaveToDoAsync(string agentPhoneNumber, string userPhoneNumber, string content);
    Task<ToDoModel[]> GetToDosAsync(string agentPhoneNumber);
    Task DeleteToDoAsync(Guid id, string agentPhoneNumber);
    Task SaveKnowledgeAsync(string agentPhoneNumber, string userPhoneNumber, string content);
    Task<KnowledgeModel[]> GetKnowledgeAsync(string agentPhoneNumber);
    Task DeleteKnowledgeAsync(Guid id, string agentPhoneNumber);
}

/// <summary>
/// Service for managing knowledge entries in Cosmos DB. Entries are partitioned by the AI's
/// phone number so that all knowledge for a given AI are stored together.
/// </summary>
public class MemoryClient(CosmosClient _cosmosClient) : IMemoryClient
{
    private const string DatabaseName = "anceladb";
    private const string KnowledgeContainerName = "knowledge";
    private const string ToDoContainerName = "todos";

    private async Task<Container> GetKnowledgeContainerAsync()
    {
        var database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(DatabaseName);
        var containerResponse = await database.Database.CreateContainerIfNotExistsAsync(
            KnowledgeContainerName,
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

        var container = await GetKnowledgeContainerAsync();
        await container.CreateItemAsync(knowledge, new PartitionKey(knowledge.agentPhoneNumber));
    }

    public async Task<KnowledgeModel[]> GetKnowledgeAsync(string agentPhoneNumber)
    {
        ArgumentNullException.ThrowIfNull(agentPhoneNumber);

        var container = await GetKnowledgeContainerAsync();

        var query = new QueryDefinition("SELECT * FROM c WHERE c.agentPhoneNumber = @phoneNumber")
                        .WithParameter("@phoneNumber", agentPhoneNumber);
        var iterator = container.GetItemQueryIterator<KnowledgeModel>(query);
        var entries = new List<KnowledgeModel>();
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

        var container = await GetKnowledgeContainerAsync();

        var response = await container.ReadItemAsync<dynamic>(id.ToString(), new PartitionKey(agentPhoneNumber));
        var knowledge = response.Resource;
        knowledge.deleted = DateTimeOffset.Now;
        await container.ReplaceItemAsync(knowledge, id.ToString(), new PartitionKey(agentPhoneNumber));
    }

    private async Task<Container> GetToDoContainerAsync()
    {
        var database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(DatabaseName);
        var containerResponse = await database.Database.CreateContainerIfNotExistsAsync(
            ToDoContainerName,
            "/agentPhoneNumber");
        return containerResponse.Container;
    }

    public async Task SaveToDoAsync(string agentPhoneNumber, string userPhoneNumber, string content)
    {
        ArgumentNullException.ThrowIfNull(agentPhoneNumber);
        ArgumentNullException.ThrowIfNull(userPhoneNumber);
        ArgumentNullException.ThrowIfNull(content);

        var todo = new
        {
            id = Guid.NewGuid().ToString(),
            content,
            userPhoneNumber,
            agentPhoneNumber,
            created = DateTimeOffset.Now,
            deleted = (DateTimeOffset?)null
        };

        var container = await GetToDoContainerAsync();
        await container.CreateItemAsync(todo, new PartitionKey(todo.agentPhoneNumber));
    }

    public async Task<ToDoModel[]> GetToDosAsync(string agentPhoneNumber)
    {
        ArgumentNullException.ThrowIfNull(agentPhoneNumber);

        var container = await GetToDoContainerAsync();

        var query = new QueryDefinition("SELECT * FROM c WHERE c.agentPhoneNumber = @phoneNumber")
                        .WithParameter("@phoneNumber", agentPhoneNumber);
        var iterator = container.GetItemQueryIterator<ToDoModel>(query);
        var todos = new List<ToDoModel>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            todos.AddRange(response);
        }
        return [.. todos];
    }

    public async Task DeleteToDoAsync(Guid id, string agentPhoneNumber)
    {
        ArgumentNullException.ThrowIfNull(agentPhoneNumber);

        var container = await GetToDoContainerAsync();

        var response = await container.ReadItemAsync<dynamic>(id.ToString(), new PartitionKey(agentPhoneNumber));
        var todo = response.Resource;
        todo.deleted = DateTimeOffset.Now;
        await container.ReplaceItemAsync(todo, id.ToString(), new PartitionKey(agentPhoneNumber));
    }
}