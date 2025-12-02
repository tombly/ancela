using System.ComponentModel;
using Microsoft.Azure.Cosmos;
using Microsoft.SemanticKernel;

namespace Ancilla.FunctionApp;

public class CosmosPlugin(CosmosClient _cosmosClient)
{
    private bool _initialized = false;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private async Task<Microsoft.Azure.Cosmos.Container> GetContainerAsync()
    {
        if (!_initialized)
        {
            await _initLock.WaitAsync();
            try
            {
                if (!_initialized)
                {
                    var database = await _cosmosClient.CreateDatabaseIfNotExistsAsync("ancilladb");
                    await database.Database.CreateContainerIfNotExistsAsync("notes", "/partitionKey");
                    _initialized = true;
                }
            }
            finally
            {
                _initLock.Release();
            }
        }

        var db = _cosmosClient.GetDatabase("ancilladb");
        return db.GetContainer("notes");
    }

    [KernelFunction("save_note")]
    [Description("Saves a note to Cosmos DB")]
    public async Task SaveNoteAsync(string aiPhoneNumber, string userPhoneNumber, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(userPhoneNumber);
        ArgumentNullException.ThrowIfNull(aiPhoneNumber);

        var note = new
        {
            id = Guid.NewGuid().ToString(),
            content,
            userPhoneNumber,
            aiPhoneNumber,
            created = DateTimeOffset.Now,
            deleted = (DateTimeOffset?)null,
            partitionKey = aiPhoneNumber
        };

        var container = await GetContainerAsync();
        await container.CreateItemAsync(note, new PartitionKey(note.partitionKey));
    }

    [KernelFunction("get_notes")]
    [Description("Retrieves notes from Cosmos DB for a given phone number")]
    public async Task<List<NoteModel>> GetNotesAsync(string aiPhoneNumber)
    {
        ArgumentNullException.ThrowIfNull(aiPhoneNumber);

        var container = await GetContainerAsync();

        var query = new QueryDefinition("SELECT * FROM c WHERE c.aiPhoneNumber = @phoneNumber")
                        .WithParameter("@phoneNumber", aiPhoneNumber);
        var iterator = container.GetItemQueryIterator<NoteModel>(query);
        var notes = new List<NoteModel>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            notes.AddRange(response);
        }
        return notes;
    }

    [KernelFunction("delete_note")]
    [Description("Deletes a note from Cosmos DB given its ID")]
    public async Task DeleteNoteAsync(string id, string aiPhoneNumber)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(aiPhoneNumber);

        var container = await GetContainerAsync();

        var response = await container.ReadItemAsync<dynamic>(id, new PartitionKey(aiPhoneNumber));
        var note = response.Resource;
        note.deleted = DateTimeOffset.Now;
        await container.ReplaceItemAsync(note, id, new PartitionKey(aiPhoneNumber));
    }
}