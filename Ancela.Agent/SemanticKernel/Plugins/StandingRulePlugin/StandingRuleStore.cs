using System.Net;
using Ancela.Agent.SemanticKernel.Plugins.StandingRulePlugin.Models;
using Microsoft.Azure.Cosmos;

namespace Ancela.Agent.SemanticKernel.Plugins.StandingRulePlugin;

public interface IStandingRuleStore
{
    Task CreateAsync(StandingRule rule);
    Task<StandingRule?> GetAsync(Guid id, string agentPhoneNumber);
    Task<StandingRule[]> ListAsync(string agentPhoneNumber, string userPhoneNumber);
    Task UpdateNextEvalSequenceAsync(Guid id, string agentPhoneNumber, long sequenceNumber);
    Task<bool> MarkEvaluatedAsync(Guid id, string agentPhoneNumber, DateTimeOffset evaluatedAt);
    Task<bool> MarkNotifiedAsync(Guid id, string agentPhoneNumber, DateTimeOffset notifiedAt);
    Task<bool> UpdateStatusAsync(Guid id, string agentPhoneNumber, RuleStatus status);
    Task<bool> DeleteAsync(Guid id, string agentPhoneNumber);
}

public class StandingRuleStore(CosmosClient _cosmosClient) : IStandingRuleStore
{
    private const string DatabaseName = "anceladb";
    private const string ContainerName = "standing_rules";

    private async Task<Container> GetContainerAsync()
    {
        var database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(DatabaseName);
        var containerResponse = await database.Database.CreateContainerIfNotExistsAsync(
            ContainerName,
            "/agentPhoneNumber");
        return containerResponse.Container;
    }

    public async Task CreateAsync(StandingRule rule)
    {
        var container = await GetContainerAsync();
        await container.CreateItemAsync(rule, new PartitionKey(rule.AgentPhoneNumber));
    }

    public async Task<StandingRule?> GetAsync(Guid id, string agentPhoneNumber)
    {
        var container = await GetContainerAsync();
        try
        {
            var response = await container.ReadItemAsync<StandingRule>(id.ToString(), new PartitionKey(agentPhoneNumber));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<StandingRule[]> ListAsync(string agentPhoneNumber, string userPhoneNumber)
    {
        var container = await GetContainerAsync();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.agentPhoneNumber = @agent AND c.userPhoneNumber = @user AND c.status != @done ORDER BY c.createdAt ASC")
                .WithParameter("@agent", agentPhoneNumber)
                .WithParameter("@user", userPhoneNumber)
                .WithParameter("@done", (int)RuleStatus.Done);

        var iterator = container.GetItemQueryIterator<StandingRule>(query);
        var results = new List<StandingRule>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }
        return [.. results];
    }

    public async Task UpdateNextEvalSequenceAsync(Guid id, string agentPhoneNumber, long sequenceNumber)
    {
        var container = await GetContainerAsync();
        var patch = new[] { PatchOperation.Set("/nextEvalSequenceNumber", sequenceNumber) };
        await container.PatchItemAsync<StandingRule>(id.ToString(), new PartitionKey(agentPhoneNumber), patch);
    }

    public async Task<bool> MarkEvaluatedAsync(Guid id, string agentPhoneNumber, DateTimeOffset evaluatedAt)
    {
        return await TryPatchAsync(id, agentPhoneNumber,
            PatchOperation.Set("/lastEvaluatedAt", evaluatedAt));
    }

    public async Task<bool> MarkNotifiedAsync(Guid id, string agentPhoneNumber, DateTimeOffset notifiedAt)
    {
        return await TryPatchAsync(id, agentPhoneNumber,
            PatchOperation.Set("/lastNotifiedAt", notifiedAt));
    }

    public async Task<bool> UpdateStatusAsync(Guid id, string agentPhoneNumber, RuleStatus status)
    {
        return await TryPatchAsync(id, agentPhoneNumber,
            PatchOperation.Set("/status", (int)status));
    }

    public async Task<bool> DeleteAsync(Guid id, string agentPhoneNumber)
    {
        var container = await GetContainerAsync();
        try
        {
            await container.DeleteItemAsync<StandingRule>(id.ToString(), new PartitionKey(agentPhoneNumber));
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
            await container.PatchItemAsync<StandingRule>(id.ToString(), new PartitionKey(agentPhoneNumber), operations);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
