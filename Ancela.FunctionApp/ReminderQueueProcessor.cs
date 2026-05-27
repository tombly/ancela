using System.Text.Json;
using Ancela.Agent.SemanticKernel.Plugins.ReminderPlugin;
using Ancela.Agent.SemanticKernel.Plugins.ReminderPlugin.Models;
using Ancela.Agent.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Ancela.FunctionApp;

public class ReminderQueueProcessor(
    ILogger<ReminderQueueProcessor> _logger,
    IReminderStore _store,
    SmsService _smsService)
{
    [Function(nameof(ReminderQueueProcessor))]
    public async Task Run([ServiceBusTrigger(ReminderQueueMessage.QueueName, Connection = "servicebus")] string body)
    {
        var message = JsonSerializer.Deserialize<ReminderQueueMessage>(body)
            ?? throw new InvalidOperationException($"Failed to deserialize reminder queue message: {body}");

        _logger.LogInformation("Reminder fire: {ReminderId}", message.ReminderId);

        var reminder = await _store.GetAsync(message.ReminderId, message.AgentPhoneNumber);
        if (reminder is null)
        {
            _logger.LogWarning("Reminder {ReminderId} not found; dropping.", message.ReminderId);
            return;
        }

        if (reminder.Status != ReminderStatus.Scheduled)
        {
            _logger.LogInformation("Reminder {ReminderId} status is {Status}; dropping.", reminder.Id, reminder.Status);
            return;
        }

        // Send first, then mark sent. Service Bus is at-least-once, so a crash or lock loss
        // between these two steps can cause a redelivery to resend (status is still Scheduled).
        // This is deliberate: for reminders a rare duplicate text is preferable to claiming-
        // before-send, which would silently drop the reminder if the send then failed.
        await _smsService.Send(reminder.UserPhoneNumber, reminder.Message);
        var markedSent = await _store.MarkSentAsync(reminder.Id, reminder.AgentPhoneNumber, DateTimeOffset.UtcNow);

        if (markedSent)
        {
            _logger.LogInformation("Reminder {ReminderId} sent.", reminder.Id);
        }
        else
        {
            _logger.LogWarning(
                "Reminder {ReminderId} SMS was sent, but the reminder could not be marked as sent. State may be inconsistent.",
                reminder.Id);
        }
    }
}
