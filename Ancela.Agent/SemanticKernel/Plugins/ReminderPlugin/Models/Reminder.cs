using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Ancela.Agent.SemanticKernel.Plugins.ReminderPlugin.Models;

public enum ReminderStatus
{
    Scheduled,
    Sent,
    Canceled,
}

[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public class Reminder
{
    public Guid Id { get; set; }
    public required string UserPhoneNumber { get; set; }
    public required string AgentPhoneNumber { get; set; }
    public required DateTimeOffset DueAt { get; set; }
    public required string Message { get; set; }
    public long SequenceNumber { get; set; }
    public ReminderStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public Guid CorrelationId { get; set; }
}
