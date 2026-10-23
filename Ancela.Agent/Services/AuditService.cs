using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Ancela.Agent.Services;

[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public class AuditEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string UserPhoneNumber { get; init; }
    public required string AgentPhoneNumber { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public Guid CorrelationId { get; init; }
    public required string Actor { get; init; }     // "user" | "agent" | "system"
    public required string Category { get; init; }  // "tool" | "web" | "sms-send" | "rule-decision" | "session"
    public required string Plugin { get; init; }
    public required string Function { get; init; }
    public string? Arguments { get; init; }         // JSON, may be omitted for sensitive calls
    public string? Result { get; init; }            // JSON, truncated if large
    public bool Success { get; init; }
    public string? Error { get; init; }
    public long DurationMs { get; init; }
}

public interface IAuditLog
{
    Task LogAsync(AuditEntry entry);
}

public class CosmosAuditLog(CosmosClient _cosmosClient, ILogger<CosmosAuditLog> _logger) : IAuditLog
{
    private const string DatabaseName = "anceladb";
    private const string ContainerName = "audit";

    public async Task LogAsync(AuditEntry entry)
    {
        try
        {
            var database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(DatabaseName);
            var container = (await database.Database.CreateContainerIfNotExistsAsync(ContainerName, "/agentPhoneNumber")).Container;
            await container.CreateItemAsync(entry, new PartitionKey(entry.AgentPhoneNumber));
            _logger.LogInformation("Audit entry written: {Plugin}.{Function} for {User}", entry.Plugin, entry.Function, entry.UserPhoneNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write audit entry {Id}", entry.Id);
        }
    }
}

/// <summary>
/// Carries a correlation ID through async call chains without requiring it to be passed
/// explicitly. Set once per message; readable by AuditFilter and any other infrastructure that
/// runs within that async context.
/// </summary>
public class CorrelationContext
{
    private static readonly AsyncLocal<Guid> _id = new();
    public Guid Current => _id.Value;

    /// <summary>
    /// Starts this message's correlation, unless the code that received the message already set
    /// one with <see cref="Use"/>. The chat queue does, with the message's own ID, so `ancela chat`
    /// can follow its message through the audit log.
    /// </summary>
    public void New()
    {
        if (_id.Value == Guid.Empty)
            _id.Value = Guid.NewGuid();
    }

    /// <summary>Correlates everything that follows in this async flow with <paramref name="id"/>.</summary>
    public void Use(Guid id) => _id.Value = id;
}
