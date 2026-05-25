using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Ancela.Agent.SemanticKernel.Plugins.StandingRulePlugin.Models;

public enum RuleStatus
{
    Active,
    Paused,
    Done,
}

[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public class StandingRule
{
    public Guid Id { get; set; }
    public required string UserPhoneNumber { get; set; }
    public required string AgentPhoneNumber { get; set; }
    public required string Description { get; set; }
    public required int EvaluationIntervalHours { get; set; }
    public DateTimeOffset? LastEvaluatedAt { get; set; }
    public DateTimeOffset? LastNotifiedAt { get; set; }
    public int NotificationCooldownDays { get; set; }
    public RuleStatus Status { get; set; }
    public long NextEvalSequenceNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CorrelationId { get; set; }
}
