using System.ComponentModel;
using Ancela.Agent.SemanticKernel.Plugins.StandingRulePlugin.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.SemanticKernel.Plugins.StandingRulePlugin;

public class StandingRulePlugin(IStandingRuleStore _store, IStandingRuleScheduler _scheduler, ILogger<StandingRulePlugin> _logger)
{
    // Lower bound on evaluation cadence. Each evaluation spends web-search and LLM
    // tokens, so we floor the interval to bound cost until per-user daily caps land.
    // TODO Phase 6 follow-up: per-user daily caps on tool calls and agent invocations.
    private const int MinEvaluationIntervalHours = 1;

    [KernelFunction("create_standing_rule")]
    [Description("Creates a recurring 'standing rule' that you evaluate on an interval and act on only when warranted, e.g. 'let me know if the Cync patio lights go on sale'. Unlike a reminder (a one-shot message at a fixed time), a standing rule is a condition you watch over time and notify the user about when it becomes true.")]
    public async Task<string> CreateStandingRuleAsync(Kernel kernel,
        [Description("What to watch for, in plain language. Be specific about the condition that should trigger a notification.")] string description,
        [Description("How often to evaluate the rule, in hours. Minimum 1. Choose a cadence that matches how fast the condition could change.")] int evaluationIntervalHours,
        [Description("Minimum days between notifications for this rule, to avoid repeated alerts. Use 0 for no cooldown.")] int notificationCooldownDays)
    {
        var (agentPhoneNumber, userPhoneNumber) = RequireContext(kernel);

        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("description must not be empty.", nameof(description));
        if (evaluationIntervalHours < MinEvaluationIntervalHours)
            throw new ArgumentException($"evaluationIntervalHours must be at least {MinEvaluationIntervalHours}; got {evaluationIntervalHours}.", nameof(evaluationIntervalHours));
        if (notificationCooldownDays < 0)
            throw new ArgumentException($"notificationCooldownDays must be zero or positive; got {notificationCooldownDays}.", nameof(notificationCooldownDays));

        var rule = new StandingRule
        {
            Id = Guid.NewGuid(),
            UserPhoneNumber = userPhoneNumber,
            AgentPhoneNumber = agentPhoneNumber,
            Description = description,
            EvaluationIntervalHours = evaluationIntervalHours,
            NotificationCooldownDays = notificationCooldownDays,
            Status = RuleStatus.Active,
            NextEvalSequenceNumber = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            CorrelationId = Guid.NewGuid(),
        };

        await _store.CreateAsync(rule);

        try
        {
            var firstEval = DateTimeOffset.UtcNow.AddHours(evaluationIntervalHours);
            var sequenceNumber = await _scheduler.ScheduleNextAsync(rule, firstEval);
            await _store.UpdateNextEvalSequenceAsync(rule.Id, agentPhoneNumber, sequenceNumber);
            rule.NextEvalSequenceNumber = sequenceNumber;
        }
        catch (Exception ex)
        {
            try
            {
                await _store.UpdateStatusAsync(rule.Id, agentPhoneNumber, RuleStatus.Paused);
            }
            catch (Exception pauseEx)
            {
                _logger.LogError(pauseEx, "Failed to pause standing rule {RuleId} after scheduling failure.", rule.Id);
            }

            _logger.LogError(ex, "Failed to schedule standing rule {RuleId}; rule was paused.", rule.Id);
            throw;
        }

        return $"Standing rule {rule.Id} created; first evaluation in {evaluationIntervalHours}h.";
    }

    [KernelFunction("list_standing_rules")]
    [Description("Lists the user's active and paused standing rules, oldest first.")]
    public async Task<StandingRule[]> ListStandingRulesAsync(Kernel kernel)
    {
        var (agentPhoneNumber, userPhoneNumber) = RequireContext(kernel);
        return await _store.ListAsync(agentPhoneNumber, userPhoneNumber);
    }

    [KernelFunction("pause_standing_rule")]
    [Description("Pauses a standing rule so it stops being evaluated until resumed. Use list_standing_rules to look up IDs.")]
    public async Task<string> PauseStandingRuleAsync(Kernel kernel,
        [Description("The standing rule ID (GUID) to pause.")] string ruleId)
    {
        var (agentPhoneNumber, _) = RequireContext(kernel);
        var (id, rule, error) = await ResolveActionableRule(ruleId, agentPhoneNumber);
        if (error is not null)
            return error;

        if (rule!.Status == RuleStatus.Paused)
            return $"Standing rule {id} is already paused.";

        await _store.UpdateStatusAsync(id, agentPhoneNumber, RuleStatus.Paused);
        await _scheduler.CancelAsync(rule.NextEvalSequenceNumber);
        return $"Standing rule {id} paused.";
    }

    [KernelFunction("resume_standing_rule")]
    [Description("Resumes a paused standing rule, scheduling its next evaluation. Use list_standing_rules to look up IDs.")]
    public async Task<string> ResumeStandingRuleAsync(Kernel kernel,
        [Description("The standing rule ID (GUID) to resume.")] string ruleId)
    {
        var (agentPhoneNumber, _) = RequireContext(kernel);
        var (id, rule, error) = await ResolveActionableRule(ruleId, agentPhoneNumber);
        if (error is not null)
            return error;

        if (rule!.Status == RuleStatus.Active)
            return $"Standing rule {id} is already active.";

        await _store.UpdateStatusAsync(id, agentPhoneNumber, RuleStatus.Active);
        var nextEval = DateTimeOffset.UtcNow.AddHours(rule.EvaluationIntervalHours);
        var sequenceNumber = await _scheduler.ScheduleNextAsync(rule, nextEval);
        await _store.UpdateNextEvalSequenceAsync(id, agentPhoneNumber, sequenceNumber);
        return $"Standing rule {id} resumed; next evaluation in {rule.EvaluationIntervalHours}h.";
    }

    [KernelFunction("delete_standing_rule")]
    [Description("Permanently deletes a standing rule. Use list_standing_rules to look up IDs.")]
    public async Task<string> DeleteStandingRuleAsync(Kernel kernel,
        [Description("The standing rule ID (GUID) to delete.")] string ruleId)
    {
        var (agentPhoneNumber, _) = RequireContext(kernel);

        if (!Guid.TryParse(ruleId, out var id))
            throw new ArgumentException($"ruleId must be a GUID; got '{ruleId}'", nameof(ruleId));

        var existing = await _store.GetAsync(id, agentPhoneNumber);
        if (existing is null)
            return $"Standing rule {id} not found.";

        await _scheduler.CancelAsync(existing.NextEvalSequenceNumber);
        await _store.DeleteAsync(id, agentPhoneNumber);
        return $"Standing rule {id} deleted.";
    }

    private async Task<(Guid id, StandingRule? rule, string? error)> ResolveActionableRule(string ruleId, string agentPhoneNumber)
    {
        if (!Guid.TryParse(ruleId, out var id))
            throw new ArgumentException($"ruleId must be a GUID; got '{ruleId}'", nameof(ruleId));

        var existing = await _store.GetAsync(id, agentPhoneNumber);
        if (existing is null)
            return (id, null, $"Standing rule {id} not found.");
        if (existing.Status == RuleStatus.Done)
            return (id, existing, $"Standing rule {id} is done and can't be changed.");

        return (id, existing, null);
    }

    private static (string agentPhoneNumber, string userPhoneNumber) RequireContext(Kernel kernel)
    {
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString();
        var userPhoneNumber = kernel.Data["userPhoneNumber"]?.ToString();

        if (string.IsNullOrWhiteSpace(agentPhoneNumber))
            throw new InvalidOperationException("agentPhoneNumber is required in kernel data");
        if (string.IsNullOrWhiteSpace(userPhoneNumber))
            throw new InvalidOperationException("userPhoneNumber is required in kernel data");

        return (agentPhoneNumber, userPhoneNumber);
    }
}
