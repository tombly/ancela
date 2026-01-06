using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Ancela.FunctionApp;

/// <summary>
/// Processes plan messages from the queue.
/// </summary>
public class PlanQueueProcessor(ILogger<PlanQueueProcessor> _logger, Agent.Agent _agent)
{
    [Function(nameof(PlanQueueProcessor))]
    public async Task Run([QueueTrigger(PlanQueueMessage.QueueName, Connection = "queues")] PlanQueueMessage message)
    {
        _logger.LogInformation("Processing message from plan queue: {PlanId}", message.PlanId);

        await _agent.PerformNextStepInPlan(Guid.Parse(message.PlanId.ToString()), message.UserPhoneNumber, message.AgentPhoneNumber);

        _logger.LogInformation("Successfully processed message from plan queue");
    }
}

public record PlanQueueMessage
{
    public const string QueueName = "plan-queue";

    public Guid PlanId { get; init; }
    public required string UserPhoneNumber { get; init; }
    public required string AgentPhoneNumber { get; init; }
}
