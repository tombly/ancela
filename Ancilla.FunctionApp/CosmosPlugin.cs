using System.ComponentModel;
using Ancilla.FunctionApp.Services;
using Microsoft.SemanticKernel;

namespace Ancilla.FunctionApp;

public class CosmosPlugin(NoteService _noteService)
{
    [KernelFunction("save_note")]
    [Description("Saves a note to the database")]
    public async Task SaveNoteAsync(Kernel kernel, string content)
    {
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString()!;
        var userPhoneNumber = kernel.Data["userPhoneNumber"]?.ToString()!;
        await _noteService.SaveNoteAsync(agentPhoneNumber, userPhoneNumber, content);
    }

    [KernelFunction("get_notes")]
    [Description("Retrieves notes from the database for the current agent")]
    public async Task<NoteEntry[]> GetNotesAsync(Kernel kernel)
    {
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString()!;
        return await _noteService.GetNotesAsync(agentPhoneNumber);
    }

    [KernelFunction("delete_note")]
    [Description("Deletes a note from the database given its ID which is a GUID. Use the get_notes function to retrieve note IDs.")]
    public async Task DeleteNoteAsync(Kernel kernel, Guid id)
    {
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString()!;
        await _noteService.DeleteNoteAsync(id, agentPhoneNumber);
    }
}