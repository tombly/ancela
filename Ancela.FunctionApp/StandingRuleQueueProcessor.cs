using System.Diagnostics;
using System.Text.Json;
using Ancela.Agent;
using Ancela.Agent.SemanticKernel.Plugins.StandingRulePlugin;
using Ancela.Agent.SemanticKernel.Plugins.StandingRulePlugin.Models;
using Ancela.Agent.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Ancela.FunctionApp;

/// <summary>
/// Fires when a standing rule is due for evaluation. Loads the rule, has the agent evaluate
/// it, then enforces the cooldown and (if allowed) sends the SMS itself — the model never
/// touches the send path. Writes a decision-audit row and reschedules the next evaluation.
/// </summary>
public class StandingRuleQueueProcessor(
    ILogger<StandingRuleQueueProcessor> _logger,
    IStandingRuleStore _store,
    IStandingRuleScheduler _scheduler,
    IUserService _userService,
    SmsService _smsService,
    IAuditLog _auditLog,
    CorrelationContext _correlation,
    Ancela.Agent.Agent _agent)
{
    [Function(nameof(StandingRuleQueueProcessor))]
    public async Task Run([ServiceBusTrigger(StandingRuleQueueMessage.QueueName, Connection = "servicebus")] string body)
    {
        var message = JsonSerializer.Deserialize<StandingRuleQueueMessage>(body)
            ?? throw new InvalidOperationException($"Failed to deserialize standing rule queue message: {body}");

        _correlation.New();
        _logger.LogInformation("Standing rule fire: {RuleId}", message.RuleId);

        var rule = await _store.GetAsync(message.RuleId, message.AgentPhoneNumber);
        if (rule is null)
        {
            _logger.LogWarning("Standing rule {RuleId} not found; dropping.", message.RuleId);
            return;
        }

        if (rule.Status != RuleStatus.Active)
        {
            _logger.LogInformation("Standing rule {RuleId} status is {Status}; dropping (no reschedule).", rule.Id, rule.Status);
            return;
        }

        var user = await _userService.GetAsync(rule.AgentPhoneNumber, rule.UserPhoneNumber);
        if (user is null || string.IsNullOrWhiteSpace(user.TimeZone))
        {
            _logger.LogWarning("No registered profile for {User}; pausing standing rule {RuleId}.", rule.UserPhoneNumber, rule.Id);
            await _store.UpdateStatusAsync(rule.Id, rule.AgentPhoneNumber, RuleStatus.Paused);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        StandingRuleEvaluation evaluation;
        try
        {
            evaluation = await _agent.EvaluateStandingRule(rule, user);
            stopwatch.Stop();
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Standing rule {RuleId} evaluation failed.", rule.Id);
            await WriteDecisionAuditAsync(rule, notified: false, reasoning: null, success: false, error: ex.Message, stopwatch.ElapsedMilliseconds);
            // Reschedule so a transient failure doesn't kill the rule.
            await RescheduleAsync(rule);
            return;
        }

        var evaluatedAt = DateTimeOffset.UtcNow;
        await _store.MarkEvaluatedAsync(rule.Id, rule.AgentPhoneNumber, evaluatedAt);

        // Enforce cooldown in code — not left to the model.
        var notifyAllowed = rule.LastNotifiedAt is null
            || DateTimeOffset.UtcNow - rule.LastNotifiedAt.Value >= TimeSpan.FromDays(rule.NotificationCooldownDays);

        bool notified = false;
        if (evaluation.ShouldNotify && notifyAllowed && !string.IsNullOrWhiteSpace(evaluation.Message))
        {
            // Send to the fixed owner number — never to a model-chosen recipient.
            await _smsService.Send(rule.UserPhoneNumber, evaluation.Message);
            await _store.MarkNotifiedAsync(rule.Id, rule.AgentPhoneNumber, evaluatedAt);
            notified = true;
            _logger.LogInformation("Standing rule {RuleId} notified user.", rule.Id);
        }
        else if (evaluation.ShouldNotify && !notifyAllowed)
        {
            _logger.LogInformation("Standing rule {RuleId} condition met but cooldown active; suppressed.", rule.Id);
        }

        await WriteDecisionAuditAsync(rule, notified, evaluation.Reasoning, success: true, error: null, stopwatch.ElapsedMilliseconds);
        await RescheduleAsync(rule);
    }

    private async Task RescheduleAsync(StandingRule rule)
    {
        // Re-fetch: the rule may have been paused or deleted during evaluation.
        var current = await _store.GetAsync(rule.Id, rule.AgentPhoneNumber);
        if (current is null || current.Status != RuleStatus.Active)
        {
            _logger.LogInformation("Standing rule {RuleId} no longer active; not rescheduling.", rule.Id);
            return;
        }

        var nextEval = DateTimeOffset.UtcNow.AddHours(current.EvaluationIntervalHours);
        var sequenceNumber = await _scheduler.ScheduleNextAsync(current, nextEval);
        await _store.UpdateNextEvalSequenceAsync(current.Id, current.AgentPhoneNumber, sequenceNumber);
        _logger.LogInformation("Standing rule {RuleId} rescheduled for {NextEval:O}.", current.Id, nextEval);
    }

    private async Task WriteDecisionAuditAsync(StandingRule rule, bool notified, string? reasoning, bool success, string? error, long durationMs)
    {
        const int maxResultChars = 4000;
        var result = reasoning is null
            ? null
            : reasoning.Length > maxResultChars ? reasoning[..maxResultChars] : reasoning;

        await _auditLog.LogAsync(new AuditEntry
        {
            UserPhoneNumber = rule.UserPhoneNumber,
            AgentPhoneNumber = rule.AgentPhoneNumber,
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = _correlation.Current,
            Actor = "agent",
            Category = "rule-decision",
            Plugin = nameof(StandingRulePlugin),
            Function = "evaluate",
            Arguments = JsonSerializer.Serialize(new { rule.Id, rule.Description, notified }),
            Result = result,
            Success = success,
            Error = error,
            DurationMs = durationMs,
        });
    }
}
