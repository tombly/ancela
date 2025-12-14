using Microsoft.Azure.Cosmos;

namespace Ancilla.FunctionApp.Services;

/// <summary>
/// Service for managing notes in Cosmos DB. Entries are partitioned by the AI's
/// phone number so that all notes for a given AI are stored together.
/// </summary>
public class NoteService(CosmosClient _cosmosClient)
{
    private const string DatabaseName = "ancilladb";
    private const string ContainerName = "notes";

    private async Task<Container> GetContainerAsync()
    {
        var database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(DatabaseName);
        var containerResponse = await database.Database.CreateContainerIfNotExistsAsync(
            ContainerName,
            "/agentPhoneNumber");
        return containerResponse.Container;
    }

    public async Task SaveNoteAsync(string agentPhoneNumber, string userPhoneNumber, string content)
    {
        ArgumentNullException.ThrowIfNull(agentPhoneNumber);
        ArgumentNullException.ThrowIfNull(userPhoneNumber);
        ArgumentNullException.ThrowIfNull(content);

        var note = new
        {
            id = Guid.NewGuid().ToString(),
            content,
            userPhoneNumber,
            agentPhoneNumber,
            created = DateTimeOffset.Now,
            deleted = (DateTimeOffset?)null
        };

        var container = await GetContainerAsync();
        await container.CreateItemAsync(note, new PartitionKey(note.agentPhoneNumber));
    }

    public async Task<NoteEntry[]> GetNotesAsync(string agentPhoneNumber)
    {
        ArgumentNullException.ThrowIfNull(agentPhoneNumber);

        var container = await GetContainerAsync();

        var query = new QueryDefinition("SELECT * FROM c WHERE c.agentPhoneNumber = @phoneNumber")
                        .WithParameter("@phoneNumber", agentPhoneNumber);
        var iterator = container.GetItemQueryIterator<NoteEntry>(query);
        var notes = new List<NoteEntry>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            notes.AddRange(response);
        }
        return [.. notes];
    }

    public async Task DeleteNoteAsync(Guid id, string agentPhoneNumber)
    {
        ArgumentNullException.ThrowIfNull(agentPhoneNumber);

        var container = await GetContainerAsync();

        var response = await container.ReadItemAsync<dynamic>(id.ToString(), new PartitionKey(agentPhoneNumber));
        var note = response.Resource;
        note.deleted = DateTimeOffset.Now;
        await container.ReplaceItemAsync(note, id.ToString(), new PartitionKey(agentPhoneNumber));
    }
}

public class NoteEntry
{
    public Guid Id { get; set; }
    public required string Content { get; set; }
    public required string UserPhoneNumber { get; set; }
    public required DateTimeOffset Created { get; set; }
    public required DateTimeOffset? Deleted { get; set; }
}