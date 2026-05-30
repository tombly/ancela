using Azure.Identity;
using Microsoft.Azure.Cosmos;

namespace Ancela.Cli.Infrastructure;

/// <summary>
/// Lazily builds and caches a read-only <see cref="CosmosClient"/> against the live
/// Azure account, authenticating with the same credential chain the developer already
/// uses (az login). The client is created on first use so that --help and argument
/// errors never trigger an auth round-trip.
/// </summary>
public sealed class CosmosClientProvider : IDisposable
{
    private readonly object _gate = new();
    private CosmosClient? _client;
    private string? _endpoint;

    /// <summary>
    /// Resolves the Cosmos account endpoint, in priority order:
    /// explicit --endpoint, ANCELA_COSMOS_ENDPOINT, then derived from ANCELA_RESOURCE_PREFIX.
    /// </summary>
    public static string? ResolveEndpoint(string? cliOverride)
    {
        if (!string.IsNullOrWhiteSpace(cliOverride))
            return cliOverride;

        var fromEnv = Environment.GetEnvironmentVariable("ANCELA_COSMOS_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv;

        var prefix = Environment.GetEnvironmentVariable("ANCELA_RESOURCE_PREFIX");
        return string.IsNullOrWhiteSpace(prefix)
            ? null
            : $"https://{prefix}-cosmos.documents.azure.com:443/";
    }

    /// <summary>The endpoint the active client is connected to, once <see cref="Get"/> has run.</summary>
    public string? Endpoint => _endpoint;

    public CosmosClient Get(string endpoint)
    {
        lock (_gate)
        {
            if (_client is not null && _endpoint == endpoint)
                return _client;

            _client?.Dispose();
            _endpoint = endpoint;
            _client = new CosmosClient(endpoint, new DefaultAzureCredential(), new CosmosClientOptions
            {
                ApplicationName = "ancela-cli",
                // Default serializer is Newtonsoft-based, matching the agent's
                // [JsonObject(CamelCaseNamingStrategy)] model annotations.
            });
            return _client;
        }
    }

    public void Dispose() => _client?.Dispose();
}
