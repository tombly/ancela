using System.ComponentModel;
using Ancela.Agent.SemanticKernel.Plugins.MemoryPlugin.Models;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.SemanticKernel.Plugins.MemoryPlugin;

/// <summary>
/// Provides functions that the model may call for remembering and recalling
/// information.
/// </summary>
public class MemoryPlugin(IMemoryClient _memoryService)
{
    [KernelFunction("save_todo")]
    [Description("Saves a to-do to the database")]
    public async Task SaveToDoAsync(Kernel kernel,
        [Description("The to-do content to save")]
        string content)
    {
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString()!;
        var userPhoneNumber = kernel.Data["userPhoneNumber"]?.ToString()!;
        await _memoryService.SaveToDoAsync(agentPhoneNumber, userPhoneNumber, content);
    }

    [KernelFunction("get_todos")]
    [Description("Retrieves to-dos from the database for the current agent")]
    public async Task<ToDoModel[]> GetToDosAsync(Kernel kernel)
    {
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString()!;
        return await _memoryService.GetToDosAsync(agentPhoneNumber);
    }

    [KernelFunction("delete_todo")]
    [Description("Deletes a to-do from the database given its ID which is a GUID. Use the get_todos function to retrieve todo IDs.")]
    public async Task DeleteToDoAsync(Kernel kernel,
        [Description("The GUID identifier of the to-do to delete")]
        Guid id)
    {
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString()!;
        await _memoryService.DeleteToDoAsync(id, agentPhoneNumber);
    }

    [KernelFunction("save_knowledge")]
    [Description("Saves a knowledge entry to the database")]
    public async Task SaveKnowledgeAsync(Kernel kernel,
        [Description("The knowledge content to save")]
        string content)
    {
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString()!;
        var userPhoneNumber = kernel.Data["userPhoneNumber"]?.ToString()!;
        await _memoryService.SaveKnowledgeAsync(agentPhoneNumber, userPhoneNumber, content);
    }

    [KernelFunction("get_knowledge")]
    [Description("Retrieves knowledge entries from the database for the current agent")]
    public async Task<KnowledgeModel[]> GetKnowledgeAsync(Kernel kernel)
    {
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString()!;
        return await _memoryService.GetKnowledgeAsync(agentPhoneNumber);
    }

    [KernelFunction("delete_knowledge")]
    [Description("Deletes a knowledge entry from the database given its ID which is a GUID. Use the get_knowledge function to retrieve knowledge IDs.")]
    public async Task DeleteKnowledgeAsync(Kernel kernel,
        [Description("The GUID identifier of the knowledge entry to delete")]
        Guid id)
    {
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString()!;
        await _memoryService.DeleteKnowledgeAsync(id, agentPhoneNumber);
    }
}
