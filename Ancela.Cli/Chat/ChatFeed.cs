using Ancela.Agent.Services;
using Ancela.Cli.Infrastructure;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;

namespace Ancela.Cli.Chat;

/// <summary>
/// What `ancela chat` reads back from Cosmos: the audit entries for a message it sent (the tool
/// calls, then the reply), what Ancela sent by SMS since you last looked, and history. Reads only;
/// the one thing the chat writes is the message itself, to the queue.
/// </summary>
public sealed class ChatFeed(CosmosBrowser _browser, string _endpoint, string _agentPhoneNumber, string _userPhoneNumber)
{
    /// <summary>
    /// Every audit entry for the message with this correlation ID, oldest first: its tool calls and,
    /// once it's answered, the outbound reply.
    /// </summary>
    public Task<List<FeedEntry>> ForMessageAsync(Guid messageId, CancellationToken ct) =>
        _browser.QueryAsync<FeedEntry>(_endpoint, "audit", new QueryDefinition(
                "SELECT * FROM c WHERE c.agentPhoneNumber = @agent AND c.correlationId = @id ORDER BY c._ts")
            .WithParameter("@agent", _agentPhoneNumber)
            .WithParameter("@id", messageId), ct);

    /// <summary>What Ancela sent you by SMS after <paramref name="sinceTs"/> (Cosmos _ts), oldest first.</summary>
    public async Task<List<FeedEntry>> SentBySmsSinceAsync(long sinceTs, int limit, CancellationToken ct)
    {
        var newestFirst = await _browser.QueryAsync<FeedEntry>(_endpoint, "audit", new QueryDefinition(
                $"SELECT TOP {limit} * FROM c WHERE c.agentPhoneNumber = @agent AND c.userPhoneNumber = @user " +
                "AND c.category = @category AND c.plugin = 'sms' AND c._ts > @since ORDER BY c._ts DESC")
            .WithParameter("@agent", _agentPhoneNumber)
            .WithParameter("@user", _userPhoneNumber)
            .WithParameter("@category", OutboundLog.Category)
            .WithParameter("@since", sinceTs), ct);
        newestFirst.Reverse();
        return newestFirst;
    }

    /// <summary>The newest outbound entry's _ts, or 0 when there are none: where "since you last looked" starts.</summary>
    public async Task<long> LatestOutboundTsAsync(CancellationToken ct)
    {
        var latest = await _browser.QueryAsync<long>(_endpoint, "audit", new QueryDefinition(
                "SELECT VALUE MAX(c._ts) FROM c WHERE c.agentPhoneNumber = @agent AND c.userPhoneNumber = @user AND c.category = @category")
            .WithParameter("@agent", _agentPhoneNumber)
            .WithParameter("@user", _userPhoneNumber)
            .WithParameter("@category", OutboundLog.Category), ct);
        return latest.FirstOrDefault();
    }

    /// <summary>The conversation history Ancela keeps (at most its last 20 entries), oldest first.</summary>
    public async Task<List<HistoryEntry>> HistoryAsync(CancellationToken ct)
    {
        var entries = await _browser.QueryAsync<HistoryEntry>(_endpoint, "history", new QueryDefinition(
                "SELECT * FROM c WHERE c.agentPhoneNumber = @agent AND c.userPhoneNumber = @user ORDER BY c.timestamp DESC")
            .WithParameter("@agent", _agentPhoneNumber)
            .WithParameter("@user", _userPhoneNumber), ct);
        entries.Reverse();
        return entries;
    }

    /// <summary>Your profile, or null if this number isn't registered with Ancela.</summary>
    public async Task<UserProfile?> ProfileAsync(CancellationToken ct) =>
        (await _browser.QueryAsync<UserProfile>(_endpoint, "users", new QueryDefinition(
                "SELECT * FROM c WHERE c.agentPhoneNumber = @agent AND c.userPhoneNumber = @user")
            .WithParameter("@agent", _agentPhoneNumber)
            .WithParameter("@user", _userPhoneNumber), ct)).FirstOrDefault();

    /// <summary>
    /// The agent's phone number when the database holds exactly one (every document is partitioned
    /// by it, and an instance has one), otherwise null.
    /// </summary>
    public static async Task<string?> DiscoverAgentPhoneNumberAsync(CosmosBrowser browser, string endpoint, CancellationToken ct)
    {
        var numbers = await browser.QueryAsync<string>(endpoint, "users",
            new QueryDefinition("SELECT DISTINCT VALUE c.agentPhoneNumber FROM c"), ct);
        return numbers.Count == 1 ? numbers[0] : null;
    }
}

/// <summary>An audit entry as the chat shows it.</summary>
public sealed class FeedEntry
{
    [JsonProperty("id")] public string Id { get; init; } = string.Empty;
    [JsonProperty("_ts")] public long Ts { get; init; }
    [JsonProperty("timestamp")] public DateTimeOffset Timestamp { get; init; }
    [JsonProperty("category")] public string Category { get; init; } = string.Empty;
    [JsonProperty("plugin")] public string Plugin { get; init; } = string.Empty;
    [JsonProperty("function")] public string Function { get; init; } = string.Empty;
    [JsonProperty("arguments")] public string? Arguments { get; init; }
    [JsonProperty("result")] public string? Result { get; init; }
    [JsonProperty("success")] public bool Success { get; init; }
    [JsonProperty("error")] public string? Error { get; init; }
}
