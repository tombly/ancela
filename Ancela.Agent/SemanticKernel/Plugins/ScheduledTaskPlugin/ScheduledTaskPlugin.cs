using System.ComponentModel;
using System.Linq;
using Ancela.Agent.SemanticKernel.Plugins.ScheduledTaskPlugin.Models;
using Ancela.Agent.Services;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.SemanticKernel.Plugins.ScheduledTaskPlugin;

public class ScheduledTaskPlugin(
    IScheduledTaskStore _store,
    IScheduledTaskScheduler _scheduler,
    IUserService _userService,
    ILogger<ScheduledTaskPlugin> _logger)
{
    [KernelFunction("create_scheduled_task")]
    [Description("Creates a recurring task that runs at a set local time on chosen days and sends the user the result by SMS, e.g. 'send me a summary of my calendar each morning'. Unlike a reminder (a one-time message you write at a fixed moment) or a standing rule (a condition watched continuously), a scheduled task re-runs on a clock schedule and freshly generates its message each time.")]
    public async Task<string> CreateScheduledTaskAsync(Kernel kernel,
        [Description("What to do each run, in plain language, e.g. 'summarize today's calendar events and any urgent emails'.")] string description,
        [Description("Local time of day to run, 24-hour 'HH:mm' in the user's timezone, e.g. '07:00'.")] string timeOfDay,
        [Description("Which days to run: 'daily', 'weekdays', 'weekends', or a comma-separated list of day names like 'Mon,Wed,Fri'. Defaults to daily.")] string? daysOfWeek = null)
    {
        var (agentPhoneNumber, userPhoneNumber) = RequireContext(kernel);

        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("description must not be empty.", nameof(description));
        if (!ScheduleCalculator.TryParseTimeOfDay(timeOfDay, out _))
            throw new ArgumentException($"timeOfDay must be 24-hour HH:mm; got '{timeOfDay}'.", nameof(timeOfDay));

        var days = ParseDays(daysOfWeek);
        if (days.Length == 0)
            throw new ArgumentException($"daysOfWeek did not resolve to any days; got '{daysOfWeek}'.", nameof(daysOfWeek));

        var user = await _userService.GetAsync(agentPhoneNumber, userPhoneNumber)
            ?? throw new InvalidOperationException("No registered profile for this user.");
        if (string.IsNullOrWhiteSpace(user.TimeZone))
            throw new InvalidOperationException("User profile has no timezone set.");

        var task = new ScheduledTask
        {
            Id = Guid.NewGuid(),
            UserPhoneNumber = userPhoneNumber,
            AgentPhoneNumber = agentPhoneNumber,
            Description = description,
            TimeOfDay = timeOfDay,
            DaysOfWeek = [.. days.Select(d => (int)d)],
            Status = ScheduledTaskStatus.Active,
            NextRunSequenceNumber = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            CorrelationId = Guid.NewGuid(),
        };

        await _store.CreateAsync(task);

        try
        {
            var firstRun = ScheduleCalculator.NextRun(timeOfDay, days, user.TimeZone, DateTimeOffset.UtcNow);
            var sequenceNumber = await _scheduler.ScheduleNextAsync(task, firstRun);
            await _store.UpdateNextRunSequenceAsync(task.Id, agentPhoneNumber, sequenceNumber);
            return $"Scheduled task {task.Id} created; first run {firstRun.ToUniversalTime():O} (UTC).";
        }
        catch (Exception ex)
        {
            try
            {
                await _store.UpdateStatusAsync(task.Id, agentPhoneNumber, ScheduledTaskStatus.Paused);
            }
            catch (Exception pauseEx)
            {
                _logger.LogError(pauseEx, "Failed to pause scheduled task {TaskId} after scheduling failure.", task.Id);
            }

            _logger.LogError(ex, "Failed to schedule task {TaskId}; task was paused.", task.Id);
            throw;
        }
    }

    [KernelFunction("list_scheduled_tasks")]
    [Description("Lists the user's recurring scheduled tasks, oldest first.")]
    public async Task<ScheduledTask[]> ListScheduledTasksAsync(Kernel kernel)
    {
        var (agentPhoneNumber, userPhoneNumber) = RequireContext(kernel);
        return await _store.ListAsync(agentPhoneNumber, userPhoneNumber);
    }

    [KernelFunction("pause_scheduled_task")]
    [Description("Pauses a recurring scheduled task so it stops running until resumed. Use list_scheduled_tasks to look up IDs.")]
    public async Task<string> PauseScheduledTaskAsync(Kernel kernel,
        [Description("The scheduled task ID (GUID) to pause.")] string taskId)
    {
        var (agentPhoneNumber, _) = RequireContext(kernel);

        if (!Guid.TryParse(taskId, out var id))
            throw new ArgumentException($"taskId must be a GUID; got '{taskId}'", nameof(taskId));

        var existing = await _store.GetAsync(id, agentPhoneNumber);
        if (existing is null)
            return $"Scheduled task {id} not found.";
        if (existing.Status == ScheduledTaskStatus.Paused)
            return $"Scheduled task {id} is already paused.";

        await _store.UpdateStatusAsync(id, agentPhoneNumber, ScheduledTaskStatus.Paused);
        await _scheduler.CancelAsync(existing.NextRunSequenceNumber);
        return $"Scheduled task {id} paused.";
    }

    [KernelFunction("resume_scheduled_task")]
    [Description("Resumes a paused scheduled task, scheduling its next run. Use list_scheduled_tasks to look up IDs.")]
    public async Task<string> ResumeScheduledTaskAsync(Kernel kernel,
        [Description("The scheduled task ID (GUID) to resume.")] string taskId)
    {
        var (agentPhoneNumber, userPhoneNumber) = RequireContext(kernel);

        if (!Guid.TryParse(taskId, out var id))
            throw new ArgumentException($"taskId must be a GUID; got '{taskId}'", nameof(taskId));

        var existing = await _store.GetAsync(id, agentPhoneNumber);
        if (existing is null)
            return $"Scheduled task {id} not found.";
        if (existing.Status == ScheduledTaskStatus.Active)
            return $"Scheduled task {id} is already active.";

        var user = await _userService.GetAsync(agentPhoneNumber, userPhoneNumber)
            ?? throw new InvalidOperationException("No registered profile for this user.");

        await _store.UpdateStatusAsync(id, agentPhoneNumber, ScheduledTaskStatus.Active);
        var days = existing.DaysOfWeek.Select(d => (DayOfWeek)d).ToArray();
        var nextRun = ScheduleCalculator.NextRun(existing.TimeOfDay, days, user.TimeZone!, DateTimeOffset.UtcNow);
        var sequenceNumber = await _scheduler.ScheduleNextAsync(existing, nextRun);
        await _store.UpdateNextRunSequenceAsync(id, agentPhoneNumber, sequenceNumber);
        return $"Scheduled task {id} resumed; next run {nextRun.ToUniversalTime():O} (UTC).";
    }

    [KernelFunction("delete_scheduled_task")]
    [Description("Permanently deletes a recurring scheduled task. Use list_scheduled_tasks to look up IDs.")]
    public async Task<string> DeleteScheduledTaskAsync(Kernel kernel,
        [Description("The scheduled task ID (GUID) to delete.")] string taskId)
    {
        var (agentPhoneNumber, _) = RequireContext(kernel);

        if (!Guid.TryParse(taskId, out var id))
            throw new ArgumentException($"taskId must be a GUID; got '{taskId}'", nameof(taskId));

        var existing = await _store.GetAsync(id, agentPhoneNumber);
        if (existing is null)
            return $"Scheduled task {id} not found.";

        await _scheduler.CancelAsync(existing.NextRunSequenceNumber);
        await _store.DeleteAsync(id, agentPhoneNumber);
        return $"Scheduled task {id} deleted.";
    }

    private static readonly DayOfWeek[] Weekdays =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
    private static readonly DayOfWeek[] Weekends = [DayOfWeek.Saturday, DayOfWeek.Sunday];
    private static readonly DayOfWeek[] AllDays = [.. Enumerable.Range(0, 7).Select(d => (DayOfWeek)d)];

    /// <summary>
    /// Parses a free-form day specification into a sorted, de-duplicated set of weekdays.
    /// Accepts "daily"/"everyday", "weekdays", "weekends", or a comma/space-separated list
    /// of full or abbreviated day names. Null or empty defaults to daily.
    /// </summary>
    internal static DayOfWeek[] ParseDays(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return AllDays;

        var normalized = spec.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "daily":
            case "everyday":
            case "every day":
                return AllDays;
            case "weekdays":
                return Weekdays;
            case "weekends":
                return Weekends;
        }

        var result = new SortedSet<DayOfWeek>();
        foreach (var token in normalized.Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseDay(token, out var day))
                result.Add(day);
        }
        return [.. result];
    }

    private static bool TryParseDay(string token, out DayOfWeek day)
    {
        day = token switch
        {
            "sun" or "sunday" => DayOfWeek.Sunday,
            "mon" or "monday" => DayOfWeek.Monday,
            "tue" or "tues" or "tuesday" => DayOfWeek.Tuesday,
            "wed" or "weds" or "wednesday" => DayOfWeek.Wednesday,
            "thu" or "thur" or "thurs" or "thursday" => DayOfWeek.Thursday,
            "fri" or "friday" => DayOfWeek.Friday,
            "sat" or "saturday" => DayOfWeek.Saturday,
            _ => (DayOfWeek)(-1),
        };
        return (int)day >= 0;
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
