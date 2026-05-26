using System.Text.Json;
using Ancela.Agent.SemanticKernel.Plugins.ScheduledTaskPlugin.Models;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;

namespace Ancela.Agent.SemanticKernel.Plugins.ScheduledTaskPlugin;

public interface IScheduledTaskScheduler
{
    Task<long> ScheduleNextAsync(ScheduledTask task, DateTimeOffset when);
    Task CancelAsync(long sequenceNumber);
}

public record ScheduledTaskQueueMessage
{
    public const string QueueName = "scheduled-tasks";

    public required Guid TaskId { get; init; }
    public required string AgentPhoneNumber { get; init; }
}

public class ScheduledTaskScheduler(ServiceBusClient _serviceBusClient, ILogger<ScheduledTaskScheduler> _logger) : IScheduledTaskScheduler
{
    public async Task<long> ScheduleNextAsync(ScheduledTask task, DateTimeOffset when)
    {
        var sender = _serviceBusClient.CreateSender(ScheduledTaskQueueMessage.QueueName);
        try
        {
            var payload = JsonSerializer.Serialize(new ScheduledTaskQueueMessage
            {
                TaskId = task.Id,
                AgentPhoneNumber = task.AgentPhoneNumber,
            });
            var message = new ServiceBusMessage(payload)
            {
                CorrelationId = task.CorrelationId.ToString(),
            };
            return await sender.ScheduleMessageAsync(message, when);
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

        var sender = _serviceBusClient.CreateSender(ScheduledTaskQueueMessage.QueueName);
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
