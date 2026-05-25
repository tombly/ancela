using System.Net;
using Ancela.Agent.SemanticKernel.Plugins.ReminderPlugin.Models;
using Microsoft.Azure.Cosmos;

namespace Ancela.Agent.SemanticKernel.Plugins.ReminderPlugin;

public interface IReminderStore
{
    Task CreateAsync(Reminder reminder);
    Task<Reminder?> GetAsync(Guid id, string agentPhoneNumber);
    Task<Reminder[]> ListScheduledAsync(string agentPhoneNumber, string userPhoneNumber);
    Task UpdateSequenceNumberAsync(Guid id, string agentPhoneNumber, long sequenceNumber);
    Task<bool> MarkSentAsync(Guid id, string agentPhoneNumber, DateTimeOffset sentAt);
    Task<bool> MarkCanceledAsync(Guid id, string agentPhoneNumber);
}

public class ReminderStore(CosmosClient _cosmosClient) : IReminderStore
{
    private const string DatabaseName = "anceladb";
    private const string ContainerName = "reminders";

    private async Task<Container> GetContainerAsync()
    {
        var database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(DatabaseName);
        var containerResponse = await database.Database.CreateContainerIfNotExistsAsync(
            ContainerName,
            "/agentPhoneNumber");
        return containerResponse.Container;
    }

    public async Task CreateAsync(Reminder reminder)
    {
        var container = await GetContainerAsync();
        await container.CreateItemAsync(reminder, new PartitionKey(reminder.AgentPhoneNumber));
    }

    public async Task<Reminder?> GetAsync(Guid id, string agentPhoneNumber)
    {
        var container = await GetContainerAsync();
        try
        {
            var response = await container.ReadItemAsync<Reminder>(id.ToString(), new PartitionKey(agentPhoneNumber));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Reminder[]> ListScheduledAsync(string agentPhoneNumber, string userPhoneNumber)
    {
        var container = await GetContainerAsync();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.agentPhoneNumber = @agent AND c.userPhoneNumber = @user AND c.status = @status ORDER BY c.dueAt ASC")
                .WithParameter("@agent", agentPhoneNumber)
                .WithParameter("@user", userPhoneNumber)
                .WithParameter("@status", (int)ReminderStatus.Scheduled);

        var iterator = container.GetItemQueryIterator<Reminder>(query);
        var results = new List<Reminder>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }
        return [.. results];
    }

    public async Task UpdateSequenceNumberAsync(Guid id, string agentPhoneNumber, long sequenceNumber)
    {
        var container = await GetContainerAsync();
        var patch = new[] { PatchOperation.Set("/sequenceNumber", sequenceNumber) };
        await container.PatchItemAsync<Reminder>(id.ToString(), new PartitionKey(agentPhoneNumber), patch);
    }

    public async Task<bool> MarkSentAsync(Guid id, string agentPhoneNumber, DateTimeOffset sentAt)
    {
        var container = await GetContainerAsync();
        try
        {
            var patch = new[]
            {
                PatchOperation.Set("/status", (int)ReminderStatus.Sent),
                PatchOperation.Set("/sentAt", sentAt),
            };
            await container.PatchItemAsync<Reminder>(id.ToString(), new PartitionKey(agentPhoneNumber), patch);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<bool> MarkCanceledAsync(Guid id, string agentPhoneNumber)
    {
        var container = await GetContainerAsync();
        try
        {
            var patch = new[] { PatchOperation.Set("/status", (int)ReminderStatus.Canceled) };
            await container.PatchItemAsync<Reminder>(id.ToString(), new PartitionKey(agentPhoneNumber), patch);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
