using System.Text.Json;
using Ancela.Agent.SemanticKernel.Plugins.StandingRulePlugin.Models;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;

namespace Ancela.Agent.SemanticKernel.Plugins.StandingRulePlugin;

public interface IStandingRuleScheduler
{
    Task<long> ScheduleNextAsync(StandingRule rule, DateTimeOffset when);
    Task CancelAsync(long sequenceNumber);
}

public record StandingRuleQueueMessage
{
    public const string QueueName = "standing-rules";

    public required Guid RuleId { get; init; }
    public required string AgentPhoneNumber { get; init; }
}

public class StandingRuleScheduler(ServiceBusClient _serviceBusClient, ILogger<StandingRuleScheduler> _logger) : IStandingRuleScheduler
{
    public async Task<long> ScheduleNextAsync(StandingRule rule, DateTimeOffset when)
    {
        var sender = _serviceBusClient.CreateSender(StandingRuleQueueMessage.QueueName);
        try
        {
            var payload = JsonSerializer.Serialize(new StandingRuleQueueMessage
            {
                RuleId = rule.Id,
                AgentPhoneNumber = rule.AgentPhoneNumber,
            });
            var message = new ServiceBusMessage(payload)
            {
                CorrelationId = rule.CorrelationId.ToString(),
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

        var sender = _serviceBusClient.CreateSender(StandingRuleQueueMessage.QueueName);
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
