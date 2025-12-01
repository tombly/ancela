using System.ComponentModel;
using Microsoft.Azure.Cosmos;
using Microsoft.SemanticKernel;

namespace Ancilla.FunctionApp;

public class CosmosPlugin(CosmosClient _cosmosClient)
{
    [KernelFunction("save_note")]
    [Description("Saves a note to Cosmos DB")]
    public async Task SaveNoteAsync(string message, string from, string to)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var note = new
        {
            id = Guid.NewGuid().ToString(),
            content = message,
            fromPhoneNumber = from,
            aiPhoneNumber = to,
            timestamp = DateTimeOffset.Now,
            partitionKey = to
        };

        var database = _cosmosClient.GetDatabase("ancilladb");
        var container = database.GetContainer("notes");

        await container.CreateItemAsync(note, new PartitionKey(note.partitionKey));
    }

    [KernelFunction("get_notes")]
    [Description("Retrieves notes from Cosmos DB for a given phone number")]
    public async Task<List<string>> GetNotesAsync(string aiPhoneNumber)
    {
        ArgumentNullException.ThrowIfNull(aiPhoneNumber);

        var database = _cosmosClient.GetDatabase("ancilladb");
        var container = database.GetContainer("notes");

        var query = new QueryDefinition("SELECT c.content FROM c WHERE c.aiPhoneNumber = @phoneNumber")
                        .WithParameter("@phoneNumber", aiPhoneNumber);
        var iterator = container.GetItemQueryIterator<dynamic>(query);
        var notes = new List<string>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            foreach (var item in response)
            {
                notes.Add((string)item.content);
            }
        }
        return notes;
    }
}