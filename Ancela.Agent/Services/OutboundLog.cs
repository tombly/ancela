namespace Ancela.Agent.Services;

/// <summary>
/// Records a message Ancela sent a user as an "outbound" audit entry: every reply to `ancela chat`,
/// and every reminder, scheduled task and standing-rule alert. `ancela chat` reads these. A CLI
/// message's reply arrives this way, found by the message's correlation ID, and the rest are what
/// came by SMS while the user may have been out of reach of their phone. Chat replies by SMS are
/// not recorded: history already holds them.
/// </summary>
public class OutboundLog(IAuditLog _auditLog, CorrelationContext _correlation)
{
    public const string Category = "outbound";

    /// <param name="channel">How it was sent: "sms" or "cli".</param>
    /// <param name="kind">What it was: "reply", "reminder", "scheduled-task" or "standing-rule".</param>
    public Task RecordAsync(string agentPhoneNumber, string userPhoneNumber, string channel, string kind, string text) =>
        _auditLog.LogAsync(new AuditEntry
        {
            UserPhoneNumber = userPhoneNumber,
            AgentPhoneNumber = agentPhoneNumber,
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = _correlation.Current,
            Actor = "agent",
            Category = Category,
            Plugin = channel,
            Function = kind,
            Result = text,
            Success = true,
        });
}
