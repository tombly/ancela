using System.Net;
using Ancela.Agent.SemanticKernel.Plugins.ScheduledTaskPlugin.Models;
using Microsoft.Azure.Cosmos;

namespace Ancela.Agent.SemanticKernel.Plugins.ScheduledTaskPlugin;

public interface IScheduledTaskStore
{
    Task CreateAsync(ScheduledTask task);
    Task<ScheduledTask?> GetAsync(Guid id, string agentPhoneNumber);
    Task<ScheduledTask[]> ListAsync(string agentPhoneNumber, string userPhoneNumber);
    Task UpdateNextRunSequenceAsync(Guid id, string agentPhoneNumber, long sequenceNumber);
    Task<bool> MarkRanAtAsync(Guid id, string agentPhoneNumber, DateTimeOffset ranAt);
    Task<bool> UpdateStatusAsync(Guid id, string agentPhoneNumber, ScheduledTaskStatus status);
    Task<bool> DeleteAsync(Guid id, string agentPhoneNumber);
}

public class ScheduledTaskStore(CosmosClient _cosmosClient) : IScheduledTaskStore
{
    private const string DatabaseName = "anceladb";
    private const string ContainerName = "scheduled_tasks";

    private async Task<Container> GetContainerAsync()
    {
        var database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(DatabaseName);
        var containerResponse = await database.Database.CreateContainerIfNotExistsAsync(
            ContainerName,
            "/agentPhoneNumber");
        return containerResponse.Container;
    }

    public async Task CreateAsync(ScheduledTask task)
    {
        var container = await GetContainerAsync();
        await container.CreateItemAsync(task, new PartitionKey(task.AgentPhoneNumber));
    }

    public async Task<ScheduledTask?> GetAsync(Guid id, string agentPhoneNumber)
    {
        var container = await GetContainerAsync();
        try
        {
            var response = await container.ReadItemAsync<ScheduledTask>(id.ToString(), new PartitionKey(agentPhoneNumber));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<ScheduledTask[]> ListAsync(string agentPhoneNumber, string userPhoneNumber)
    {
        var container = await GetContainerAsync();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.agentPhoneNumber = @agent AND c.userPhoneNumber = @user ORDER BY c.createdAt ASC")
                .WithParameter("@agent", agentPhoneNumber)
                .WithParameter("@user", userPhoneNumber);

        var iterator = container.GetItemQueryIterator<ScheduledTask>(query);
        var results = new List<ScheduledTask>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }
        return [.. results];
    }

    public async Task UpdateNextRunSequenceAsync(Guid id, string agentPhoneNumber, long sequenceNumber)
    {
        var container = await GetContainerAsync();
        var patch = new[] { PatchOperation.Set("/nextRunSequenceNumber", sequenceNumber) };
        await container.PatchItemAsync<ScheduledTask>(id.ToString(), new PartitionKey(agentPhoneNumber), patch);
    }

    public async Task<bool> MarkRanAtAsync(Guid id, string agentPhoneNumber, DateTimeOffset ranAt)
    {
        return await TryPatchAsync(id, agentPhoneNumber, PatchOperation.Set("/lastRunAt", ranAt));
    }

    public async Task<bool> UpdateStatusAsync(Guid id, string agentPhoneNumber, ScheduledTaskStatus status)
    {
        return await TryPatchAsync(id, agentPhoneNumber, PatchOperation.Set("/status", (int)status));
    }

    public async Task<bool> DeleteAsync(Guid id, string agentPhoneNumber)
    {
        var container = await GetContainerAsync();
        try
        {
            await container.DeleteItemAsync<ScheduledTask>(id.ToString(), new PartitionKey(agentPhoneNumber));
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private async Task<bool> TryPatchAsync(Guid id, string agentPhoneNumber, params PatchOperation[] operations)
    {
        var container = await GetContainerAsync();
        try
        {
            await container.PatchItemAsync<ScheduledTask>(id.ToString(), new PartitionKey(agentPhoneNumber), operations);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
