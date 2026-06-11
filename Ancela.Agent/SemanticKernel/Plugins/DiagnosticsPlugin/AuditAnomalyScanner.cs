using System.Globalization;
using Ancela.Agent.SemanticKernel.Plugins.DiagnosticsPlugin.Models;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;

namespace Ancela.Agent.SemanticKernel.Plugins.DiagnosticsPlugin;

public interface IAuditAnomalyScanner
{
    /// <summary>
    /// Gathers raw activity counts from the audit container for the trailing
    /// <paramref name="windowHours"/>. Judgment (thresholds, severities) is applied separately
    /// by <see cref="AnomalyEvaluator"/>.
    /// </summary>
    Task<AuditActivitySummary> ScanAsync(string agentPhoneNumber, int windowHours);
}

public class AuditAnomalyScanner(CosmosClient _cosmosClient) : IAuditAnomalyScanner
{
    private const string DatabaseName = "anceladb";
    private const string ContainerName = "audit";

    // Caps so a noisy window can't balloon the tool result handed back to the model.
    private const int MaxUnknownSenders = 10;
    private const int MaxRecentFailures = 3;

    // The guard filter's denial messages (see AutonomousToolGuardFilter) — how a hard-denied
    // invocation is distinguished from an ordinary tool error in the audit log.
    private const string ProfileDenialMarker = "is not permitted in the";
    private const string OwnerOnlyDenialMarker = "may only be invoked by the owner";

    public async Task<AuditActivitySummary> ScanAsync(string agentPhoneNumber, int windowHours)
    {
        var container = await GetContainerAsync();
        var partitionKey = new PartitionKey(agentPhoneNumber);

        // Audit timestamps are always written from DateTimeOffset.UtcNow, so they serialize with
        // a +00:00 offset. Comparing against a second-precision UTC prefix keeps the range filter
        // a lexicographic string comparison that Cosmos can serve from the index.
        var cutoff = DateTimeOffset.UtcNow.AddHours(-windowHours)
            .UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        var unknownSendersTask = QueryUnknownSendersAsync(container, partitionKey, agentPhoneNumber, cutoff);
        var stepUpTask = QueryFailedStepUpsAsync(container, partitionKey, agentPhoneNumber, cutoff);
        var denialsTask = QueryGuardDenialsAsync(container, partitionKey, agentPhoneNumber, cutoff);
        var sessionOpsTask = QuerySessionOpsAsync(container, partitionKey, agentPhoneNumber, cutoff);
        var volumesTask = QueryVolumeCountsAsync(container, partitionKey, agentPhoneNumber, cutoff);
        var failuresTask = QueryFailedFunctionsAsync(container, partitionKey, agentPhoneNumber, cutoff);
        var recentFailuresTask = QueryRecentFailuresAsync(container, partitionKey, agentPhoneNumber, cutoff);
        await Task.WhenAll(unknownSendersTask, stepUpTask, denialsTask, sessionOpsTask, volumesTask, failuresTask, recentFailuresTask);

        var (unknownMessageCount, unknownSenders) = unknownSendersTask.Result;
        var sessionOps = sessionOpsTask.Result;
        var volumes = volumesTask.Result;

        return new AuditActivitySummary
        {
            WindowHours = windowHours,
            UnknownSenderMessageCount = unknownMessageCount,
            UnknownSenders = unknownSenders,
            FailedStepUpCount = stepUpTask.Result,
            GuardDenialCount = denialsTask.Result,
            InviteCount = sessionOps.GetValueOrDefault("invite"),
            RevokeCount = sessionOps.GetValueOrDefault("revoke"),
            DeregisterCount = sessionOps.GetValueOrDefault("deregister"),
            WebSearchCount = volumes.GetValueOrDefault("web_search"),
            SmsSendCount = volumes.GetValueOrDefault("send_sms"),
            EmailSendCount = volumes.GetValueOrDefault("send_email"),
            FailedFunctions = failuresTask.Result,
            RecentFailures = recentFailuresTask.Result,
        };
    }

    private async Task<Container> GetContainerAsync()
    {
        var database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(DatabaseName);
        var containerResponse = await database.Database.CreateContainerIfNotExistsAsync(
            ContainerName,
            "/agentPhoneNumber");
        return containerResponse.Container;
    }

    private static async Task<(int MessageCount, string[] Senders)> QueryUnknownSendersAsync(
        Container container, PartitionKey partitionKey, string agentPhoneNumber, string cutoff)
    {
        // Dropped inbound messages: ChatInterceptor logs these as failed "session" entries.
        // Both queries are bounded server-side (COUNT / DISTINCT TOP) so an SMS flood — the very
        // event this scan detects — can't make the scan itself stream thousands of rows.
        const string filter = """
            WHERE c.agentPhoneNumber = @agent AND c.timestamp >= @cutoff
              AND c.category = 'session' AND c.success = false
              AND (c.error = 'no active account' OR c.error = 'not invited')
            """;

        var countQuery = new QueryDefinition($"SELECT VALUE COUNT(1) FROM c {filter}")
            .WithParameter("@agent", agentPhoneNumber)
            .WithParameter("@cutoff", cutoff);
        var sendersQuery = new QueryDefinition(
            $"SELECT DISTINCT TOP {MaxUnknownSenders} VALUE c.userPhoneNumber FROM c {filter}")
            .WithParameter("@agent", agentPhoneNumber)
            .WithParameter("@cutoff", cutoff);

        var countTask = ReadAllAsync<int>(container, countQuery, partitionKey);
        var sendersTask = ReadAllAsync<string>(container, sendersQuery, partitionKey);
        await Task.WhenAll(countTask, sendersTask);

        return (countTask.Result.FirstOrDefault(), [.. sendersTask.Result]);
    }

    private static async Task<int> QueryFailedStepUpsAsync(
        Container container, PartitionKey partitionKey, string agentPhoneNumber, string cutoff)
    {
        var query = new QueryDefinition(
            """
            SELECT VALUE COUNT(1) FROM c
            WHERE c.agentPhoneNumber = @agent AND c.timestamp >= @cutoff
              AND c.category = 'session' AND c.success = false
              AND STARTSWITH(c.error, 'step-up')
            """)
            .WithParameter("@agent", agentPhoneNumber)
            .WithParameter("@cutoff", cutoff);

        return (await ReadAllAsync<int>(container, query, partitionKey)).FirstOrDefault();
    }

    private static async Task<int> QueryGuardDenialsAsync(
        Container container, PartitionKey partitionKey, string agentPhoneNumber, string cutoff)
    {
        var query = new QueryDefinition(
            $"""
            SELECT VALUE COUNT(1) FROM c
            WHERE c.agentPhoneNumber = @agent AND c.timestamp >= @cutoff
              AND c.success = false
              AND (CONTAINS(c.error, '{ProfileDenialMarker}') OR CONTAINS(c.error, '{OwnerOnlyDenialMarker}'))
            """)
            .WithParameter("@agent", agentPhoneNumber)
            .WithParameter("@cutoff", cutoff);

        return (await ReadAllAsync<int>(container, query, partitionKey)).FirstOrDefault();
    }

    private static async Task<Dictionary<string, int>> QuerySessionOpsAsync(
        Container container, PartitionKey partitionKey, string agentPhoneNumber, string cutoff)
    {
        var query = new QueryDefinition(
            """
            SELECT c["function"] AS fn, COUNT(1) AS count FROM c
            WHERE c.agentPhoneNumber = @agent AND c.timestamp >= @cutoff
              AND c.category = 'session' AND c.success = true
              AND c["function"] IN ('invite', 'revoke', 'deregister')
            GROUP BY c["function"]
            """)
            .WithParameter("@agent", agentPhoneNumber)
            .WithParameter("@cutoff", cutoff);

        var rows = await ReadAllAsync<FunctionCountRow>(container, query, partitionKey);
        return rows.ToDictionary(r => r.Fn, r => r.Count, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<Dictionary<string, int>> QueryVolumeCountsAsync(
        Container container, PartitionKey partitionKey, string agentPhoneNumber, string cutoff)
    {
        var query = new QueryDefinition(
            """
            SELECT c["function"] AS fn, COUNT(1) AS count FROM c
            WHERE c.agentPhoneNumber = @agent AND c.timestamp >= @cutoff
              AND c.success = true
              AND c["function"] IN ('web_search', 'send_sms', 'send_email')
            GROUP BY c["function"]
            """)
            .WithParameter("@agent", agentPhoneNumber)
            .WithParameter("@cutoff", cutoff);

        var rows = await ReadAllAsync<FunctionCountRow>(container, query, partitionKey);
        return rows.ToDictionary(r => r.Fn, r => r.Count, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<FunctionFailureGroup[]> QueryFailedFunctionsAsync(
        Container container, PartitionKey partitionKey, string agentPhoneNumber, string cutoff)
    {
        var query = new QueryDefinition(
            """
            SELECT c.plugin AS plugin, c["function"] AS fn, COUNT(1) AS count FROM c
            WHERE c.agentPhoneNumber = @agent AND c.timestamp >= @cutoff
              AND c.category = 'tool' AND c.success = false
            GROUP BY c.plugin, c["function"]
            """)
            .WithParameter("@agent", agentPhoneNumber)
            .WithParameter("@cutoff", cutoff);

        var rows = await ReadAllAsync<FunctionFailureRow>(container, query, partitionKey);
        return rows
            .OrderByDescending(r => r.Count)
            .Select(r => new FunctionFailureGroup { Plugin = r.Plugin ?? "", Function = r.Fn, Count = r.Count })
            .ToArray();
    }

    private static async Task<RecentFailure[]> QueryRecentFailuresAsync(
        Container container, PartitionKey partitionKey, string agentPhoneNumber, string cutoff)
    {
        var query = new QueryDefinition(
            $"""
            SELECT TOP {MaxRecentFailures} c.plugin AS plugin, c["function"] AS fn, c.error AS error, c.timestamp AS timestamp FROM c
            WHERE c.agentPhoneNumber = @agent AND c.timestamp >= @cutoff
              AND c.category = 'tool' AND c.success = false
            ORDER BY c.timestamp DESC
            """)
            .WithParameter("@agent", agentPhoneNumber)
            .WithParameter("@cutoff", cutoff);

        var rows = await ReadAllAsync<RecentFailureRow>(container, query, partitionKey);
        return rows
            .Select(r => new RecentFailure { Plugin = r.Plugin ?? "", Function = r.Fn, Error = r.Error, Timestamp = r.Timestamp })
            .ToArray();
    }

    private static async Task<List<T>> ReadAllAsync<T>(Container container, QueryDefinition query, PartitionKey partitionKey)
    {
        var results = new List<T>();
        var iterator = container.GetItemQueryIterator<T>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = partitionKey });
        while (iterator.HasMoreResults)
            results.AddRange(await iterator.ReadNextAsync());
        return results;
    }

    private class FunctionCountRow
    {
        [JsonProperty("fn")] public string Fn { get; set; } = "";
        [JsonProperty("count")] public int Count { get; set; }
    }

    private class FunctionFailureRow
    {
        [JsonProperty("plugin")] public string? Plugin { get; set; }
        [JsonProperty("fn")] public string Fn { get; set; } = "";
        [JsonProperty("count")] public int Count { get; set; }
    }

    private class RecentFailureRow
    {
        [JsonProperty("plugin")] public string? Plugin { get; set; }
        [JsonProperty("fn")] public string Fn { get; set; } = "";
        [JsonProperty("error")] public string? Error { get; set; }
        [JsonProperty("timestamp")] public DateTimeOffset Timestamp { get; set; }
    }
}
