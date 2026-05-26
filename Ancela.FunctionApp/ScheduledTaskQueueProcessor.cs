using System.Diagnostics;
using System.Text.Json;
using Ancela.Agent.SemanticKernel.Plugins.ScheduledTaskPlugin;
using Ancela.Agent.SemanticKernel.Plugins.ScheduledTaskPlugin.Models;
using Ancela.Agent.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Ancela.FunctionApp;

/// <summary>
/// Fires when a recurring scheduled task is due. Loads the task, has the agent carry it out,
/// sends the resulting message to the user, writes an audit row, then reschedules the next
/// occurrence in the user's local timezone.
/// </summary>
public class ScheduledTaskQueueProcessor(
    ILogger<ScheduledTaskQueueProcessor> _logger,
    IScheduledTaskStore _store,
    IScheduledTaskScheduler _scheduler,
    IUserService _userService,
    SmsService _smsService,
    IAuditLog _auditLog,
    CorrelationContext _correlation,
    Ancela.Agent.Agent _agent)
{
    [Function(nameof(ScheduledTaskQueueProcessor))]
    public async Task Run([ServiceBusTrigger(ScheduledTaskQueueMessage.QueueName, Connection = "servicebus")] string body)
    {
        var message = JsonSerializer.Deserialize<ScheduledTaskQueueMessage>(body)
            ?? throw new InvalidOperationException($"Failed to deserialize scheduled task queue message: {body}");

        _correlation.New();
        _logger.LogInformation("Scheduled task fire: {TaskId}", message.TaskId);

        var task = await _store.GetAsync(message.TaskId, message.AgentPhoneNumber);
        if (task is null)
        {
            _logger.LogWarning("Scheduled task {TaskId} not found; dropping.", message.TaskId);
            return;
        }

        if (task.Status != ScheduledTaskStatus.Active)
        {
            _logger.LogInformation("Scheduled task {TaskId} status is {Status}; dropping (no reschedule).", task.Id, task.Status);
            return;
        }

        var user = await _userService.GetAsync(task.AgentPhoneNumber, task.UserPhoneNumber);
        if (user is null || string.IsNullOrWhiteSpace(user.TimeZone))
        {
            _logger.LogWarning("No registered profile for {User}; pausing scheduled task {TaskId}.", task.UserPhoneNumber, task.Id);
            await _store.UpdateStatusAsync(task.Id, task.AgentPhoneNumber, ScheduledTaskStatus.Paused);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var reply = await _agent.PerformScheduledTask(task, user);
            stopwatch.Stop();
            await _smsService.Send(task.UserPhoneNumber, reply);
            await WriteAuditAsync(task, reply, success: true, error: null, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Scheduled task {TaskId} run failed.", task.Id);
            await WriteAuditAsync(task, result: null, success: false, error: ex.Message, stopwatch.ElapsedMilliseconds);
            // Fall through to reschedule so a transient failure doesn't kill the task.
        }

        await _store.MarkRanAtAsync(task.Id, task.AgentPhoneNumber, DateTimeOffset.UtcNow);
        await RescheduleAsync(task, user.TimeZone);
    }

    private async Task RescheduleAsync(ScheduledTask task, string timeZone)
    {
        // Re-fetch: the task may have been paused or deleted during the run.
        var current = await _store.GetAsync(task.Id, task.AgentPhoneNumber);
        if (current is null || current.Status != ScheduledTaskStatus.Active)
        {
            _logger.LogInformation("Scheduled task {TaskId} no longer active; not rescheduling.", task.Id);
            return;
        }

        var days = current.DaysOfWeek.Select(d => (DayOfWeek)d).ToArray();
        var nextRun = ScheduleCalculator.NextRun(current.TimeOfDay, days, timeZone, DateTimeOffset.UtcNow);
        var sequenceNumber = await _scheduler.ScheduleNextAsync(current, nextRun);
        await _store.UpdateNextRunSequenceAsync(current.Id, current.AgentPhoneNumber, sequenceNumber);
        _logger.LogInformation("Scheduled task {TaskId} rescheduled for {NextRun:O}.", current.Id, nextRun.ToUniversalTime());
    }

    private async Task WriteAuditAsync(ScheduledTask task, string? result, bool success, string? error, long durationMs)
    {
        const int maxResultChars = 4000;
        var trimmed = result is null
            ? null
            : result.Length > maxResultChars ? result[..maxResultChars] : result;

        await _auditLog.LogAsync(new AuditEntry
        {
            UserPhoneNumber = task.UserPhoneNumber,
            AgentPhoneNumber = task.AgentPhoneNumber,
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = _correlation.Current,
            Actor = "agent",
            Category = "scheduled-task",
            Plugin = nameof(ScheduledTaskPlugin),
            Function = "perform",
            Arguments = JsonSerializer.Serialize(new { task.Id, task.Description }),
            Result = trimmed,
            Success = success,
            Error = error,
            DurationMs = durationMs,
        });
    }
}
