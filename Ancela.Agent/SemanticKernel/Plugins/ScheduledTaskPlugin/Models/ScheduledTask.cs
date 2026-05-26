using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Ancela.Agent.SemanticKernel.Plugins.ScheduledTaskPlugin.Models;

public enum ScheduledTaskStatus
{
    Active,
    Paused,
}

[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public class ScheduledTask
{
    public Guid Id { get; set; }
    public required string UserPhoneNumber { get; set; }
    public required string AgentPhoneNumber { get; set; }
    public required string Description { get; set; }

    /// <summary>Local time-of-day to run, "HH:mm" (24-hour), interpreted in the user's IANA timezone.</summary>
    public required string TimeOfDay { get; set; }

    /// <summary>Days the task runs, as System.DayOfWeek values (0=Sunday..6=Saturday).</summary>
    public required int[] DaysOfWeek { get; set; }

    public ScheduledTaskStatus Status { get; set; }
    public long NextRunSequenceNumber { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CorrelationId { get; set; }
}
