using System.ComponentModel;
using System.Text.RegularExpressions;
using Ancela.Agent.SemanticKernel.Plugins.MemoryPlugin.Models;
using Ancela.Agent.Services;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.SemanticKernel.Plugins.MemoryPlugin;

/// <summary>
/// Provides functions that the model may call for remembering and recalling
/// information.
/// </summary>
public class MemoryPlugin(IMemoryClient _memoryService, SmsService _smsService, OwnerService _ownerService)
{
    [KernelFunction("save_todo")]
    [Description("Saves a to-do to the database")]
    public async Task SaveToDoAsync(Kernel kernel,
        [Description("The to-do content to save")]
        string content)
    {
        EnsureKernelData(kernel);
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString()!;
        var userPhoneNumber = kernel.Data["userPhoneNumber"]?.ToString()!;
        await _memoryService.SaveToDoAsync(agentPhoneNumber, userPhoneNumber, content);

        if (!_ownerService.IsOwner(userPhoneNumber))
            await _smsService.Send(_ownerService.OwnerPhoneNumber, $"New to-do added by {userPhoneNumber}: {content}");
    }

    [KernelFunction("get_todos")]
    [Description("Retrieves to-dos from the database for the current agent. Links in them come back as (link) unless includeLinks is true.")]
    public async Task<ToDoModel[]> GetToDosAsync(Kernel kernel,
        [Description("True only when the user wants a link from a to-do. Leave it false to list to-dos, so a long link doesn't fill the text.")]
        bool includeLinks = false)
    {
        EnsureKernelData(kernel);
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString()!;
        var todos = await _memoryService.GetToDosAsync(agentPhoneNumber);
        if (includeLinks)
            return todos;

        // The to-do keeps the whole link; only the listing shortens it. One ~600-character
        // event link made every "what's on my list?" several texts longer, and SmsService cuts
        // a reply off at 1600 characters.
        return [.. todos.Select(t => new ToDoModel
        {
            Id = t.Id,
            Content = Link.Replace(t.Content, "(link)"),
            Created = t.Created,
            Deleted = t.Deleted,
        })];
    }

    private static readonly Regex Link = new(@"https?://\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [KernelFunction("delete_todo")]
    [Description("Deletes a to-do from the database given its ID which is a GUID. Use the get_todos function to retrieve todo IDs.")]
    public async Task DeleteToDoAsync(Kernel kernel,
        [Description("The GUID identifier of the to-do to delete")]
        Guid id)
    {
        EnsureKernelData(kernel);
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString()!;
        await _memoryService.DeleteToDoAsync(id, agentPhoneNumber);
    }

    [KernelFunction("save_knowledge")]
    [Description("Saves a knowledge entry to the database")]
    public async Task SaveKnowledgeAsync(Kernel kernel,
        [Description("The knowledge content to save")]
        string content)
    {
        EnsureKernelData(kernel);
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString()!;
        var userPhoneNumber = kernel.Data["userPhoneNumber"]?.ToString()!;
        await _memoryService.SaveKnowledgeAsync(agentPhoneNumber, userPhoneNumber, content);

        if (!_ownerService.IsOwner(userPhoneNumber))
            await _smsService.Send(_ownerService.OwnerPhoneNumber, $"New knowledge added by {userPhoneNumber}: {content}");
    }

    [KernelFunction("get_knowledge")]
    [Description("Retrieves the knowledge entries: the facts saved with save_knowledge. They are not listed in Current context, so call this to find out what is saved before saying something is or isn't, and to get an entry's ID for delete_knowledge.")]
    public async Task<KnowledgeModel[]> GetKnowledgeAsync(Kernel kernel)
    {
        EnsureKernelData(kernel);
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString()!;
        return await _memoryService.GetKnowledgeAsync(agentPhoneNumber);
    }

    [KernelFunction("delete_knowledge")]
    [Description("Deletes a knowledge entry from the database given its ID which is a GUID. Use the get_knowledge function to retrieve knowledge IDs.")]
    public async Task DeleteKnowledgeAsync(Kernel kernel,
        [Description("The GUID identifier of the knowledge entry to delete")]
        Guid id)
    {
        EnsureKernelData(kernel);
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString()!;
        await _memoryService.DeleteKnowledgeAsync(id, agentPhoneNumber);
    }

    private static void EnsureKernelData(Kernel kernel)
    {
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString();
        var userPhoneNumber = kernel.Data["userPhoneNumber"]?.ToString();

        if (string.IsNullOrWhiteSpace(agentPhoneNumber))
        {
            throw new InvalidOperationException("agentPhoneNumber is required in kernel data");
        }

        if (string.IsNullOrWhiteSpace(userPhoneNumber))
        {
            throw new InvalidOperationException("userPhoneNumber is required in kernel data");
        }
    }
}
