using System.ComponentModel;
using Microsoft.Azure.Cosmos;
using Microsoft.SemanticKernel;

namespace Ancilla.FunctionApp;

public class CosmosPlugin(CosmosClient _cosmosClient)
{
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

        var container = _cosmosClient.GetDatabase("ancilladb").GetContainer("notes");
        await container.CreateItemAsync(note, new PartitionKey(note.partitionKey));
    }

    [KernelFunction("get_notes")]
    [Description("Retrieves notes from Cosmos DB for a given phone number")]
    public async Task<List<NoteModel>> GetNotesAsync(string aiPhoneNumber)
    {
        ArgumentNullException.ThrowIfNull(aiPhoneNumber);

        var container = _cosmosClient.GetDatabase("ancilladb").GetContainer("notes");

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
    [Description("Deletes a note from Cosmos DB given its ID which is a GUID. Use the get_notes function to retrieve note IDs.")]
    public async Task DeleteNoteAsync(Guid id, string aiPhoneNumber)
    {
        ArgumentNullException.ThrowIfNull(aiPhoneNumber);

        var container = _cosmosClient.GetDatabase("ancilladb").GetContainer("notes");

        var response = await container.ReadItemAsync<dynamic>(id.ToString(), new PartitionKey(aiPhoneNumber));
        var note = response.Resource;
        note.deleted = DateTimeOffset.Now;
        await container.ReplaceItemAsync(note, id.ToString(), new PartitionKey(aiPhoneNumber));
    }
}