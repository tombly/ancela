using System.Text.Json;
using Ancela.Agent.SemanticKernel.Plugins.ReminderPlugin.Models;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;

namespace Ancela.Agent.SemanticKernel.Plugins.ReminderPlugin;

public interface IReminderScheduler
{
    Task<long> ScheduleAsync(Reminder reminder);
    Task CancelAsync(long sequenceNumber);
}

public record ReminderQueueMessage
{
    public const string QueueName = "reminders";

    public required Guid ReminderId { get; init; }
    public required string AgentPhoneNumber { get; init; }
}

public class ReminderScheduler(ServiceBusClient _serviceBusClient, ILogger<ReminderScheduler> _logger) : IReminderScheduler
{
    public async Task<long> ScheduleAsync(Reminder reminder)
    {
        var sender = _serviceBusClient.CreateSender(ReminderQueueMessage.QueueName);
        try
        {
            var payload = JsonSerializer.Serialize(new ReminderQueueMessage
            {
                ReminderId = reminder.Id,
                AgentPhoneNumber = reminder.AgentPhoneNumber,
            });
            var message = new ServiceBusMessage(payload)
            {
                MessageId = reminder.Id.ToString(),
                CorrelationId = reminder.CorrelationId.ToString(),
            };
            return await sender.ScheduleMessageAsync(message, reminder.DueAt);
        }
        finally
        {
            await sender.DisposeAsync();
        }
    }

    public async Task CancelAsync(long sequenceNumber)
    {
        if (sequenceNumber <= 0)
        {
            return;
        }

        var sender = _serviceBusClient.CreateSender(ReminderQueueMessage.QueueName);
        try
        {
            await sender.CancelScheduledMessageAsync(sequenceNumber);
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessageNotFound)
        {
            _logger.LogInformation("Scheduled message {Seq} already delivered or canceled; relying on doc status check on fire path.", sequenceNumber);
        }
        finally
        {
            await sender.DisposeAsync();
        }
    }
}
