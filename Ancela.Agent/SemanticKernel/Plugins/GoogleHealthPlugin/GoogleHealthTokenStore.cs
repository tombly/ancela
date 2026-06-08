using System.Net;
using Ancela.Agent.SemanticKernel.Plugins.GoogleHealthPlugin.Models;
using Microsoft.Azure.Cosmos;

namespace Ancela.Agent.SemanticKernel.Plugins.GoogleHealthPlugin;

public interface IGoogleHealthTokenStore
{
    /// <summary>Reads the Google Health token document, or null if none has been bootstrapped yet.</summary>
    Task<OAuthToken?> GetAsync();

    /// <summary>
    /// Persists the token. When <see cref="OAuthToken.ETag"/> is set the write is conditional
    /// (optimistic concurrency) and throws <see cref="CosmosException"/> with
    /// <see cref="HttpStatusCode.PreconditionFailed"/> if another writer won the race. On success
    /// the token's ETag is refreshed from the response. Callers treat persistence as best-effort:
    /// because Google refresh tokens are durable, a failed write is not fatal.
    /// </summary>
    Task SaveAsync(OAuthToken token);
}

/// <summary>
/// Cosmos-backed store for the single owner OAuth token, in the shared <c>oauth_tokens</c> container
/// (partitioned by <c>/provider</c>). Deliberately NOT registered in the read-only CLI catalog so the
/// audit viewer cannot dump the access/refresh tokens.
/// </summary>
public class GoogleHealthTokenStore(CosmosClient _cosmosClient) : IGoogleHealthTokenStore
{
    private const string DatabaseName = "anceladb";
    private const string ContainerName = "oauth_tokens";
    private const string Provider = "google-health";

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
            var response = await container.ReadItemAsync<OAuthToken>(Provider, new PartitionKey(Provider));
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

        ItemResponse<OAuthToken> response = token.ETag is null
            ? await container.CreateItemAsync(token, partitionKey)
            : await container.ReplaceItemAsync(
                token, token.Id, partitionKey, new ItemRequestOptions { IfMatchEtag = token.ETag });

        token.ETag = response.ETag;
    }
}
