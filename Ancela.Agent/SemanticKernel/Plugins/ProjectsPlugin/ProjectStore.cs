using System.Net;
using Ancela.Agent.SemanticKernel.Plugins.ProjectsPlugin.Models;
using Microsoft.Azure.Cosmos;

namespace Ancela.Agent.SemanticKernel.Plugins.ProjectsPlugin;

public interface IProjectStore
{
    Task CreateAsync(Project project);
    Task<ProjectSummary[]> ListAsync(string agentPhoneNumber);
    Task<Project?> GetAsync(Guid id, string agentPhoneNumber);
    Task<bool> UpdateAsync(Guid id, string agentPhoneNumber, string? name, string? purpose, bool? isArchived, string? notes);
    Task<bool> AddEntryAsync(Guid projectId, string agentPhoneNumber, ProjectEntry entry);
    Task<bool> UpdateEntryAsync(Guid projectId, string agentPhoneNumber, Guid entryId, string? content, string? status);
    Task<bool> SoftDeleteEntryAsync(Guid projectId, string agentPhoneNumber, Guid entryId);
}

/// <summary>
/// Cosmos operations for projects. Partitioned by the agent's phone number so all
/// projects for an instance are stored together and shared across its users.
/// Entries are embedded in the project document.
/// </summary>
public class ProjectStore(CosmosClient _cosmosClient) : IProjectStore
{
    private const string DatabaseName = "anceladb";
    private const string ContainerName = "projects";

    private async Task<Container> GetContainerAsync()
    {
        var database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(DatabaseName);
        var containerResponse = await database.Database.CreateContainerIfNotExistsAsync(
            ContainerName, "/agentPhoneNumber");
        return containerResponse.Container;
    }

    public async Task CreateAsync(Project project)
    {
        var container = await GetContainerAsync();
        await container.CreateItemAsync(project, new PartitionKey(project.AgentPhoneNumber));
    }

    public async Task<ProjectSummary[]> ListAsync(string agentPhoneNumber)
    {
        var container = await GetContainerAsync();
        var query = new QueryDefinition(
            "SELECT c.id, c.name, c.purpose FROM c WHERE c.agentPhoneNumber = @agent AND c.isArchived = false ORDER BY c.createdAt ASC")
            .WithParameter("@agent", agentPhoneNumber);

        var iterator = container.GetItemQueryIterator<ProjectSummary>(query);
        var results = new List<ProjectSummary>();
        while (iterator.HasMoreResults)
            results.AddRange(await iterator.ReadNextAsync());
        return [.. results];
    }

    public async Task<Project?> GetAsync(Guid id, string agentPhoneNumber)
    {
        var container = await GetContainerAsync();
        try
        {
            var response = await container.ReadItemAsync<Project>(id.ToString(), new PartitionKey(agentPhoneNumber));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> UpdateAsync(Guid id, string agentPhoneNumber, string? name, string? purpose, bool? isArchived, string? notes)
    {
        var operations = new List<PatchOperation>();
        if (name is not null) operations.Add(PatchOperation.Set("/name", name));
        if (purpose is not null) operations.Add(PatchOperation.Set("/purpose", purpose));
        if (isArchived is not null) operations.Add(PatchOperation.Set("/isArchived", isArchived.Value));
        if (notes is not null) operations.Add(PatchOperation.Set("/notes", notes));

        // Nothing to change: report whether the project exists so the caller can
        // distinguish "no-op" from "not found".
        if (operations.Count == 0)
            return await GetAsync(id, agentPhoneNumber) is not null;

        operations.Add(PatchOperation.Set("/updatedAt", DateTimeOffset.UtcNow));
        return await TryPatchAsync(id, agentPhoneNumber, [.. operations]);
    }

    public async Task<bool> AddEntryAsync(Guid projectId, string agentPhoneNumber, ProjectEntry entry)
    {
        // Array-append patch so concurrent adds don't clobber the whole document.
        return await TryPatchAsync(projectId, agentPhoneNumber,
            PatchOperation.Add("/entries/-", entry),
            PatchOperation.Set("/updatedAt", DateTimeOffset.UtcNow));
    }

    public async Task<bool> UpdateEntryAsync(Guid projectId, string agentPhoneNumber, Guid entryId, string? content, string? status)
    {
        var container = await GetContainerAsync();
        var project = await GetAsync(projectId, agentPhoneNumber);
        var entry = project?.Entries.FirstOrDefault(e => e.Id == entryId && !e.Deleted);
        if (project is null || entry is null) return false;

        if (content is not null) entry.Content = content;
        if (status is not null) entry.Status = status;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await container.ReplaceItemAsync(project, projectId.ToString(), new PartitionKey(agentPhoneNumber));
        return true;
    }

    public async Task<bool> SoftDeleteEntryAsync(Guid projectId, string agentPhoneNumber, Guid entryId)
    {
        var container = await GetContainerAsync();
        var project = await GetAsync(projectId, agentPhoneNumber);
        var entry = project?.Entries.FirstOrDefault(e => e.Id == entryId && !e.Deleted);
        if (project is null || entry is null) return false;

        entry.Deleted = true;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await container.ReplaceItemAsync(project, projectId.ToString(), new PartitionKey(agentPhoneNumber));
        return true;
    }

    private async Task<bool> TryPatchAsync(Guid id, string agentPhoneNumber, params PatchOperation[] operations)
    {
        var container = await GetContainerAsync();
        try
        {
            await container.PatchItemAsync<Project>(id.ToString(), new PartitionKey(agentPhoneNumber), operations);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
