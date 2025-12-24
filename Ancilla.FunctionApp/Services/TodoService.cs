using Microsoft.Azure.Cosmos;

namespace Ancilla.FunctionApp.Services;

public interface ITodoService
{
    Task SaveTodoAsync(string agentPhoneNumber, string userPhoneNumber, string content);
    Task<TodoEntry[]> GetTodosAsync(string agentPhoneNumber);
    Task DeleteTodoAsync(Guid id, string agentPhoneNumber);
}

/// <summary>
/// Service for managing todos in Cosmos DB. Entries are partitioned by the AI's
/// phone number so that all todos for a given AI are stored together.
/// </summary>
public class TodoService(CosmosClient _cosmosClient) : ITodoService
{
    private const string DatabaseName = "ancilladb";
    private const string ContainerName = "todos";

    private async Task<Container> GetContainerAsync()
    {
        var database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(DatabaseName);
        var containerResponse = await database.Database.CreateContainerIfNotExistsAsync(
            ContainerName,
            "/agentPhoneNumber");
        return containerResponse.Container;
    }

    public async Task SaveTodoAsync(string agentPhoneNumber, string userPhoneNumber, string content)
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

        var container = await GetContainerAsync();
        await container.CreateItemAsync(todo, new PartitionKey(todo.agentPhoneNumber));
    }

    public async Task<TodoEntry[]> GetTodosAsync(string agentPhoneNumber)
    {
        ArgumentNullException.ThrowIfNull(agentPhoneNumber);

        var container = await GetContainerAsync();

        var query = new QueryDefinition("SELECT * FROM c WHERE c.agentPhoneNumber = @phoneNumber")
                        .WithParameter("@phoneNumber", agentPhoneNumber);
        var iterator = container.GetItemQueryIterator<TodoEntry>(query);
        var todos = new List<TodoEntry>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            todos.AddRange(response);
        }
        return [.. todos];
    }

    public async Task DeleteTodoAsync(Guid id, string agentPhoneNumber)
    {
        ArgumentNullException.ThrowIfNull(agentPhoneNumber);

        var container = await GetContainerAsync();

        var response = await container.ReadItemAsync<dynamic>(id.ToString(), new PartitionKey(agentPhoneNumber));
        var todo = response.Resource;
        todo.deleted = DateTimeOffset.Now;
        await container.ReplaceItemAsync(todo, id.ToString(), new PartitionKey(agentPhoneNumber));
    }
}

public class TodoEntry
{
    public Guid Id { get; set; }
    public required string Content { get; set; }
    public required string UserPhoneNumber { get; set; }
    public required DateTimeOffset Created { get; set; }
    public required DateTimeOffset? Deleted { get; set; }
}
