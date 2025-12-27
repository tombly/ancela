using System.ComponentModel;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.SemanticKernel.Plugins.MemoryPlugin;

/// <summary>
/// Provides functions that the model may call for remembering and recalling
/// information.
/// </summary>
public class MemoryPlugin(ITodoClient _todoService, IKnowledgeClient _knowledgeService)
{
    [KernelFunction("save_todo")]
    [Description("Saves a todo to the database")]
    public async Task SaveTodoAsync(
        Kernel kernel,
        [Description("The todo content to save")]
        string content)
    {
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString()!;
        var userPhoneNumber = kernel.Data["userPhoneNumber"]?.ToString()!;
        await _todoService.SaveTodoAsync(agentPhoneNumber, userPhoneNumber, content);
    }

    [KernelFunction("get_todos")]
    [Description("Retrieves todos from the database for the current agent")]
    public async Task<TodoEntry[]> GetTodosAsync(Kernel kernel)
    {
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString()!;
        return await _todoService.GetTodosAsync(agentPhoneNumber);
    }

    [KernelFunction("delete_todo")]
    [Description("Deletes a todo from the database given its ID which is a GUID. Use the get_todos function to retrieve todo IDs.")]
    public async Task DeleteTodoAsync(
        Kernel kernel,
        [Description("The GUID identifier of the todo to delete")]
        Guid id)
    {
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString()!;
        await _todoService.DeleteTodoAsync(id, agentPhoneNumber);
    }

    [KernelFunction("save_knowledge")]
    [Description("Saves a knowledge entry to the database")]
    public async Task SaveKnowledgeAsync(
        Kernel kernel,
        [Description("The knowledge content to save")]
        string content)
    {
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString()!;
        var userPhoneNumber = kernel.Data["userPhoneNumber"]?.ToString()!;
        await _knowledgeService.SaveKnowledgeAsync(agentPhoneNumber, userPhoneNumber, content);
    }

    [KernelFunction("get_knowledge")]
    [Description("Retrieves knowledge entries from the database for the current agent")]
    public async Task<KnowledgeEntry[]> GetKnowledgeAsync(Kernel kernel)
    {
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString()!;
        return await _knowledgeService.GetKnowledgeAsync(agentPhoneNumber);
    }

    [KernelFunction("delete_knowledge")]
    [Description("Deletes a knowledge entry from the database given its ID which is a GUID. Use the get_knowledge function to retrieve knowledge IDs.")]
    public async Task DeleteKnowledgeAsync(
        Kernel kernel,
        [Description("The GUID identifier of the knowledge entry to delete")]
        Guid id)
    {
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString()!;
        await _knowledgeService.DeleteKnowledgeAsync(id, agentPhoneNumber);
    }
}
