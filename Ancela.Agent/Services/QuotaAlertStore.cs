using System.Net;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Ancela.Agent.Services;

[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public class QuotaAlertState
{
    public string Id { get; init; } = QuotaAlertStore.DocumentId;
    public required string AgentPhoneNumber { get; init; }
    public required DateTimeOffset LastNotifiedAt { get; set; }
}

/// <summary>
/// Remembers when the owner was last told about OpenAI credit exhaustion, so a failure that
/// repeats on every queue message produces one text rather than one per attempt.
/// </summary>
public interface IQuotaAlertStore
{
    /// <summary>
    /// Claims the right to send an alert. Returns true at most once per <paramref name="window"/>
    /// per agent; the claim is atomic, so concurrent replicas cannot both win.
    /// </summary>
    Task<bool> TryClaimAsync(string agentPhoneNumber, DateTimeOffset now, TimeSpan window);
}

public class QuotaAlertStore(CosmosClient _cosmosClient) : IQuotaAlertStore
{
    internal const string DocumentId = "openai-credit";

    private const string DatabaseName = "anceladb";
    private const string ContainerName = "alert_state";

    private async Task<Container> GetContainerAsync()
    {
        var database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(DatabaseName);
        var containerResponse = await database.Database.CreateContainerIfNotExistsAsync(
            ContainerName,
            "/agentPhoneNumber");
        return containerResponse.Container;
    }

    public async Task<bool> TryClaimAsync(string agentPhoneNumber, DateTimeOffset now, TimeSpan window)
    {
        var container = await GetContainerAsync();
        var partitionKey = new PartitionKey(agentPhoneNumber);

        try
        {
            var existing = await container.ReadItemAsync<QuotaAlertState>(DocumentId, partitionKey);
            if (now - existing.Resource.LastNotifiedAt < window)
                return false;

            existing.Resource.LastNotifiedAt = now;

            // IfMatchEtag: if another replica claimed the same window between the read and this
            // write, Cosmos returns 412 and that replica sends the single alert.
            await container.ReplaceItemAsync(
                existing.Resource,
                DocumentId,
                partitionKey,
                new ItemRequestOptions { IfMatchEtag = existing.ETag });

            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            try
            {
                await container.CreateItemAsync(
                    new QuotaAlertState { AgentPhoneNumber = agentPhoneNumber, LastNotifiedAt = now },
                    partitionKey);
                return true;
            }
            catch (CosmosException conflict) when (conflict.StatusCode == HttpStatusCode.Conflict)
            {
                return false;
            }
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }
}
