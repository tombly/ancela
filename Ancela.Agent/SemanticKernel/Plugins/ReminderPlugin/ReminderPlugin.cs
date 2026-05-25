using System.ComponentModel;
using System.Globalization;
using Ancela.Agent.SemanticKernel.Plugins.ReminderPlugin.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.SemanticKernel.Plugins.ReminderPlugin;

public class ReminderPlugin(IReminderStore _store, IReminderScheduler _scheduler, ILogger<ReminderPlugin> _logger)
{
    [KernelFunction("create_reminder")]
    [Description("Schedules a single SMS reminder to the user at a specific absolute time. Resolve relative times (\"tomorrow afternoon\") to an absolute ISO-8601 timestamp using the user's local timezone before calling, and confirm the resolved time back to the user in your response.")]
    public async Task<string> CreateReminderAsync(Kernel kernel,
        [Description("Absolute due time as an ISO-8601 timestamp with offset, e.g. 2026-05-25T14:00:00-07:00.")] string dueAt,
        [Description("The reminder text to send to the user via SMS.")] string message)
    {
        var (agentPhoneNumber, userPhoneNumber) = RequireContext(kernel);

        if (!DateTimeOffset.TryParse(dueAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var due))
            throw new ArgumentException($"dueAt must be an ISO-8601 timestamp; got '{dueAt}'", nameof(dueAt));

        if (due <= DateTimeOffset.UtcNow)
            throw new ArgumentException($"dueAt must be in the future; got {due:O} (now {DateTimeOffset.UtcNow:O}).", nameof(dueAt));

        var reminder = new Reminder
        {
            Id = Guid.NewGuid(),
            UserPhoneNumber = userPhoneNumber,
            AgentPhoneNumber = agentPhoneNumber,
            DueAt = due,
            Message = message,
            SequenceNumber = 0,
            Status = ReminderStatus.Scheduled,
            CreatedAt = DateTimeOffset.UtcNow,
            CorrelationId = Guid.NewGuid(),
        };

        await _store.CreateAsync(reminder);

        try
        {
            var sequenceNumber = await _scheduler.ScheduleAsync(reminder);
            await _store.UpdateSequenceNumberAsync(reminder.Id, agentPhoneNumber, sequenceNumber);
            reminder.SequenceNumber = sequenceNumber;
        }
        catch (Exception ex)
        {
            try
            {
                await _store.MarkCanceledAsync(reminder.Id, agentPhoneNumber);
                reminder.Status = ReminderStatus.Canceled;
            }
            catch (Exception markCanceledEx)
            {
                _logger.LogError(markCanceledEx, "Failed to mark reminder {ReminderId} as canceled after scheduling failure.", reminder.Id);
            }

            _logger.LogError(ex, "Failed to schedule reminder {ReminderId}; reminder was marked as Canceled.", reminder.Id);
            throw;
        }

        return $"Reminder {reminder.Id} scheduled for {due:O}.";
    }

    [KernelFunction("list_reminders")]
    [Description("Lists the user's upcoming (Scheduled) reminders, soonest first.")]
    public async Task<Reminder[]> ListRemindersAsync(Kernel kernel)
    {
        var (agentPhoneNumber, userPhoneNumber) = RequireContext(kernel);
        return await _store.ListScheduledAsync(agentPhoneNumber, userPhoneNumber);
    }

    [KernelFunction("cancel_reminder")]
    [Description("Cancels a scheduled reminder by its ID. Use list_reminders to look up IDs.")]
    public async Task<string> CancelReminderAsync(Kernel kernel,
        [Description("The reminder ID (GUID) to cancel.")] string reminderId)
    {
        var (agentPhoneNumber, _) = RequireContext(kernel);

        if (!Guid.TryParse(reminderId, out var id))
            throw new ArgumentException($"reminderId must be a GUID; got '{reminderId}'", nameof(reminderId));

        var existing = await _store.GetAsync(id, agentPhoneNumber);
        if (existing is null)
            return $"Reminder {id} not found.";

        if (existing.Status != ReminderStatus.Scheduled)
            return $"Reminder {id} is already {existing.Status}.";

        await _store.MarkCanceledAsync(id, agentPhoneNumber);
        await _scheduler.CancelAsync(existing.SequenceNumber);
        return $"Reminder {id} canceled.";
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
