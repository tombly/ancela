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

    public async Task SaveNoteAsync(string aiPhoneNumber, string userPhoneNumber, string content)
    {
        ArgumentNullException.ThrowIfNull(aiPhoneNumber);
        ArgumentNullException.ThrowIfNull(userPhoneNumber);
        ArgumentNullException.ThrowIfNull(content);

        var note = new
        {
            id = Guid.NewGuid().ToString(),
            content,
            userPhoneNumber,
            aiPhoneNumber,
            created = DateTimeOffset.Now,
            deleted = (DateTimeOffset?)null
        };

        var container = _cosmosClient.GetDatabase(DatabaseName).GetContainer(ContainerName);
        await container.CreateItemAsync(note, new PartitionKey(note.aiPhoneNumber));
    }

    public async Task<NoteEntry[]> GetNotesAsync(string aiPhoneNumber)
    {
        ArgumentNullException.ThrowIfNull(aiPhoneNumber);

        var container = _cosmosClient.GetDatabase(DatabaseName).GetContainer(ContainerName);

        var query = new QueryDefinition("SELECT * FROM c WHERE c.aiPhoneNumber = @phoneNumber")
                        .WithParameter("@phoneNumber", aiPhoneNumber);
        var iterator = container.GetItemQueryIterator<NoteEntry>(query);
        var notes = new List<NoteEntry>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            notes.AddRange(response);
        }
        return [.. notes];
    }

    public async Task DeleteNoteAsync(Guid id, string aiPhoneNumber)
    {
        ArgumentNullException.ThrowIfNull(aiPhoneNumber);

        var container = _cosmosClient.GetDatabase(DatabaseName).GetContainer(ContainerName);

        var response = await container.ReadItemAsync<dynamic>(id.ToString(), new PartitionKey(aiPhoneNumber));
        var note = response.Resource;
        note.deleted = DateTimeOffset.Now;
        await container.ReplaceItemAsync(note, id.ToString(), new PartitionKey(aiPhoneNumber));
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