using System.ComponentModel;
using Ancilla.FunctionApp.Services;
using Microsoft.SemanticKernel;

namespace Ancilla.FunctionApp;

public class CosmosPlugin(NoteService _noteService)
{
    [KernelFunction("save_note")]
    [Description("Saves a note to the database")]
    public async Task SaveNoteAsync(string agentPhoneNumber, string userPhoneNumber, string content)
    {
        await _noteService.SaveNoteAsync(agentPhoneNumber, userPhoneNumber, content);
    }

    [KernelFunction("get_notes")]
    [Description("Retrieves notes from the database for a given phone number")]
    public async Task<NoteEntry[]> GetNotesAsync(string agentPhoneNumber)
    {
        return await _noteService.GetNotesAsync(agentPhoneNumber);
    }

    [KernelFunction("delete_note")]
    [Description("Deletes a note from the database given its ID which is a GUID. Use the get_notes function to retrieve note IDs.")]
    public async Task DeleteNoteAsync(Guid id, string agentPhoneNumber)
    {
        await _noteService.DeleteNoteAsync(id, agentPhoneNumber);
    }
}