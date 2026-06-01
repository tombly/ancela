using System.ComponentModel;
using System.Linq;
using Ancela.Agent.SemanticKernel.Plugins.ProjectsPlugin.Models;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.SemanticKernel.Plugins.ProjectsPlugin;

/// <summary>
/// Functions the model may call to manage projects — workspaces for larger,
/// longer-lived efforts — and their embedded trackable entries.
/// </summary>
public class ProjectsPlugin(IProjectStore _store)
{
    [KernelFunction("create_project")]
    [Description("Creates a new project: a workspace for a larger or longer-lived effort (planning a trip, tracking a list until it's complete, collecting feature ideas, etc.). Use a project instead of a to-do when the user wants to capture ideas, plan, or track a list over time. Returns the new project's ID.")]
    public async Task<string> CreateProjectAsync(Kernel kernel,
        [Description("A short name for the project, e.g. 'Backpacking Trip' or 'Verify Account Beneficiaries'.")] string name,
        [Description("Optional one-line description of the project's purpose or goal.")] string? purpose = null)
    {
        var (agentPhoneNumber, userPhoneNumber) = RequireContext(kernel);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("name must not be empty.", nameof(name));

        var now = DateTimeOffset.UtcNow;
        var project = new Project
        {
            Id = Guid.NewGuid(),
            AgentPhoneNumber = agentPhoneNumber,
            UserPhoneNumber = userPhoneNumber,
            Name = name.Trim(),
            Purpose = string.IsNullOrWhiteSpace(purpose) ? null : purpose.Trim(),
            Notes = "",
            IsArchived = false,
            Entries = [],
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _store.CreateAsync(project);
        return $"Created project '{project.Name}' ({project.Id}).";
    }

    [KernelFunction("list_projects")]
    [Description("Lists the user's active (non-archived) projects with their IDs, names, and purposes, oldest first. Use this to resolve a project the user refers to by name.")]
    public async Task<ProjectSummary[]> ListProjectsAsync(Kernel kernel)
    {
        var (agentPhoneNumber, _) = RequireContext(kernel);
        return await _store.ListAsync(agentPhoneNumber);
    }

    [KernelFunction("get_project")]
    [Description("Gets a single project's full detail: purpose, freeform notes body, and its (non-deleted) entries. Use list_projects first to resolve a name to an ID.")]
    public async Task<Project> GetProjectAsync(Kernel kernel,
        [Description("The project ID (GUID).")] string projectId)
    {
        var (agentPhoneNumber, _) = RequireContext(kernel);
        var id = ParseId(projectId, nameof(projectId));
        var project = await _store.GetAsync(id, agentPhoneNumber)
            ?? throw new InvalidOperationException($"Project {id} not found.");
        // Hide soft-deleted entries from the model.
        project.Entries = [.. project.Entries.Where(e => !e.Deleted)];
        return project;
    }

    [KernelFunction("update_project")]
    [Description("Updates a project's name, purpose, freeform notes body, or archived state. Only the fields you pass change. The notes parameter REPLACES the entire notes body — to edit notes, first read them with get_project, modify the text, then pass the full updated text. Archive a project (isArchived = true) when it is finished or set aside instead of deleting it.")]
    public async Task<string> UpdateProjectAsync(Kernel kernel,
        [Description("The project ID (GUID).")] string projectId,
        [Description("New name, or omit to leave unchanged.")] string? name = null,
        [Description("New purpose, or omit to leave unchanged.")] string? purpose = null,
        [Description("True to archive, false to un-archive. Omit to leave unchanged.")] bool? isArchived = null,
        [Description("Full replacement text for the freeform notes body. Omit to leave unchanged.")] string? notes = null)
    {
        var (agentPhoneNumber, _) = RequireContext(kernel);
        var id = ParseId(projectId, nameof(projectId));
        var ok = await _store.UpdateAsync(id, agentPhoneNumber, name, purpose, isArchived, notes);
        return ok ? $"Updated project {id}." : $"Project {id} not found.";
    }

    [KernelFunction("add_project_entry")]
    [Description("Adds a trackable entry to a project, e.g. an account to verify or an item to sell. Optionally give it a short status label like 'open', 'done', 'listed', or 'sold'. Returns the new entry's ID.")]
    public async Task<string> AddProjectEntryAsync(Kernel kernel,
        [Description("The project ID (GUID) to add the entry to.")] string projectId,
        [Description("The entry text, e.g. 'Fidelity brokerage account'.")] string content,
        [Description("Optional short status label, e.g. 'open' or 'done'.")] string? status = null)
    {
        var (agentPhoneNumber, _) = RequireContext(kernel);
        var pid = ParseId(projectId, nameof(projectId));
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("content must not be empty.", nameof(content));

        var entry = new ProjectEntry
        {
            Id = Guid.NewGuid(),
            Content = content.Trim(),
            Status = string.IsNullOrWhiteSpace(status) ? null : status.Trim(),
            Deleted = false,
        };
        var ok = await _store.AddEntryAsync(pid, agentPhoneNumber, entry);
        return ok ? $"Added entry {entry.Id} to project {pid}." : $"Project {pid} not found.";
    }

    [KernelFunction("update_project_entry")]
    [Description("Updates an entry's text and/or status within a project. Use get_project to look up the project and entry IDs.")]
    public async Task<string> UpdateProjectEntryAsync(Kernel kernel,
        [Description("The project ID (GUID) the entry belongs to.")] string projectId,
        [Description("The entry ID (GUID) to update.")] string entryId,
        [Description("New entry text, or omit to leave unchanged.")] string? content = null,
        [Description("New status label, or omit to leave unchanged.")] string? status = null)
    {
        var (agentPhoneNumber, _) = RequireContext(kernel);
        var pid = ParseId(projectId, nameof(projectId));
        var eid = ParseId(entryId, nameof(entryId));
        var ok = await _store.UpdateEntryAsync(pid, agentPhoneNumber, eid, content, status);
        return ok ? $"Updated entry {eid}." : $"Entry {eid} not found in project {pid}.";
    }

    [KernelFunction("delete_project_entry")]
    [Description("Removes an entry from a project. Use get_project to look up the project and entry IDs.")]
    public async Task<string> DeleteProjectEntryAsync(Kernel kernel,
        [Description("The project ID (GUID) the entry belongs to.")] string projectId,
        [Description("The entry ID (GUID) to remove.")] string entryId)
    {
        var (agentPhoneNumber, _) = RequireContext(kernel);
        var pid = ParseId(projectId, nameof(projectId));
        var eid = ParseId(entryId, nameof(entryId));
        var ok = await _store.SoftDeleteEntryAsync(pid, agentPhoneNumber, eid);
        return ok ? $"Deleted entry {eid}." : $"Entry {eid} not found in project {pid}.";
    }

    private static Guid ParseId(string value, string paramName)
    {
        if (!Guid.TryParse(value, out var id))
            throw new ArgumentException($"{paramName} must be a GUID; got '{value}'.", paramName);
        return id;
    }

    private static (string agentPhoneNumber, string userPhoneNumber) RequireContext(Kernel kernel)
    {
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString();
        var userPhoneNumber = kernel.Data["userPhoneNumber"]?.ToString();

        if (string.IsNullOrWhiteSpace(agentPhoneNumber))
            throw new InvalidOperationException("agentPhoneNumber is required in kernel data");
        if (string.IsNullOrWhiteSpace(userPhoneNumber))
            throw new InvalidOperationException("userPhoneNumber is required in kernel data");

        return (agentPhoneNumber, userPhoneNumber);
    }
}
