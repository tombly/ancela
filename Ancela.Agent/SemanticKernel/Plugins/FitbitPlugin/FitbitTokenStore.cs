using System.Net;
using Ancela.Agent.SemanticKernel.Plugins.FitbitPlugin.Models;
using Microsoft.Azure.Cosmos;

namespace Ancela.Agent.SemanticKernel.Plugins.FitbitPlugin;

public interface IFitbitTokenStore
{
    /// <summary>Reads the Fitbit token document, or null if none has been bootstrapped yet.</summary>
    Task<OAuthToken?> GetAsync();

    /// <summary>
    /// Persists the token. When <see cref="OAuthToken.ETag"/> is set the write is conditional
    /// (optimistic concurrency) and throws <see cref="CosmosException"/> with
    /// <see cref="HttpStatusCode.PreconditionFailed"/> if another writer won the race; a first-time
    /// create that races throws <see cref="HttpStatusCode.Conflict"/>. On success the token's ETag
    /// is refreshed from the response.
    /// </summary>
    Task SaveAsync(OAuthToken token);
}

/// <summary>
/// Cosmos-backed store for the single owner OAuth token, in the <c>oauth_tokens</c> container
/// (partitioned by <c>/provider</c>). Mirrors the lazy-create shape of the other stores. This
/// container is deliberately NOT registered in the read-only CLI catalog so the audit viewer
/// cannot dump the access/refresh tokens.
/// </summary>
public class FitbitTokenStore(CosmosClient _cosmosClient) : IFitbitTokenStore
{
    private const string DatabaseName = "anceladb";
    private const string ContainerName = "oauth_tokens";
    private const string FitbitProvider = "fitbit";

    private async Task<Container> GetContainerAsync()
    {
        var database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(DatabaseName);
        var containerResponse = await database.Database.CreateContainerIfNotExistsAsync(
            ContainerName,
            "/provider");
        return containerResponse.Container;
    }

    public async Task<OAuthToken?> GetAsync()
    {
        var container = await GetContainerAsync();
        try
        {
            var response = await container.ReadItemAsync<OAuthToken>(FitbitProvider, new PartitionKey(FitbitProvider));
            var token = response.Resource;
            token.ETag = response.ETag;
            return token;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task SaveAsync(OAuthToken token)
    {
        var container = await GetContainerAsync();
        var partitionKey = new PartitionKey(token.Provider);

        // No ETag → first write (create, races as Conflict). ETag → conditional replace
        // (races as PreconditionFailed). The client treats both as "someone else won, re-read".
        ItemResponse<OAuthToken> response = token.ETag is null
            ? await container.CreateItemAsync(token, partitionKey)
            : await container.ReplaceItemAsync(
                token, token.Id, partitionKey, new ItemRequestOptions { IfMatchEtag = token.ETag });

        token.ETag = response.ETag;
    }
}
