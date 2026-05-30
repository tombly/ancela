using Microsoft.Azure.Cosmos;

namespace Ancela.Cli.Infrastructure;

/// <summary>
/// Read-only access to the Ancela Cosmos database. Deliberately exposes no write
/// paths — this is a viewer.
/// </summary>
public sealed class CosmosBrowser(CosmosClientProvider _provider)
{
    public const string DatabaseName = "anceladb";

    /// <summary>The containers provisioned by configure_cosmos.sh, with a short description.</summary>
    public static readonly IReadOnlyList<ContainerInfo> Containers =
    [
        new("audit", "Audit log of every tool/web/sms/rule action"),
        new("users", "Registered + pending user profiles"),
        new("history", "Per-user chat history"),
        new("todos", "Shared to-do items"),
        new("knowledge", "Shared knowledge notes"),
        new("reminders", "One-shot scheduled reminders"),
        new("standing_rules", "Standing rules evaluated on a schedule"),
        new("scheduled_tasks", "Recurring scheduled tasks"),
    ];

    private Container GetContainer(string endpoint, string container) =>
        _provider.Get(endpoint).GetContainer(DatabaseName, container);

    /// <summary>Counts documents in a container with a cross-partition COUNT query.</summary>
    public async Task<long> CountAsync(string endpoint, string container, CancellationToken ct = default)
    {
        var iterator = GetContainer(endpoint, container)
            .GetItemQueryIterator<long>(new QueryDefinition("SELECT VALUE COUNT(1) FROM c"));
        long total = 0;
        while (iterator.HasMoreResults)
        {
            foreach (var value in await iterator.ReadNextAsync(ct))
                total += value;
        }
        return total;
    }

    /// <summary>Runs an arbitrary read query, deserializing into <typeparamref name="T"/>.</summary>
    public async Task<List<T>> QueryAsync<T>(string endpoint, string container, QueryDefinition query, CancellationToken ct = default)
    {
        var results = new List<T>();
        var iterator = GetContainer(endpoint, container).GetItemQueryIterator<T>(query);
        while (iterator.HasMoreResults)
            results.AddRange(await iterator.ReadNextAsync(ct));
        return results;
    }
}

public readonly record struct ContainerInfo(string Name, string Description);
