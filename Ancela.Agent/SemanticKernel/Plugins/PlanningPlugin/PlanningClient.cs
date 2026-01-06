using System.Net;
using System.Text.Json;
using Ancela.Agent.SemanticKernel.Plugins.PlanningPlugin.Models;
using Azure.Storage.Queues;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace Ancela.Agent.SemanticKernel.Plugins.PlanningPlugin;

public interface IPlanningClient
{
    Task CreatePlan(string name, string userPhoneNumber, string agentPhoneNumber, List<StepModel> steps);
    Task<(DateTimeOffset deliveryTime, string receiptId, string messageId)> ScheduleMessageForNextStep(string userPhoneNumber, string agentPhoneNumber, Guid planId, decimal delayHours);
    Task<PlanModel?> GetPlanAsync(Guid planId, string agentPhoneNumber);
    Task<bool> CompleteStepAsync(Guid planId, string agentPhoneNumber, int stepNumber);
    Task<bool> PlanHasIncompleteSteps(Guid planId, string agentPhoneNumber);
    Task<bool> SavePlanHistoryEntry(Guid planId, string agentPhoneNumber, string entry);
    Task<string[]> GetPlanHistory(Guid planId, string agentPhoneNumber);
}

/// <summary>
/// </summary>
public class PlanningClient(QueueServiceClient _queueServiceClient, CosmosClient _cosmosClient, ILogger<PlanningClient> _logger) : IPlanningClient
{
    private const string DatabaseName = "anceladb";
    private const string ContainerName = "plans";

    private async Task<Container> GetContainerAsync()
    {
        var database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(DatabaseName);
        var containerResponse = await database.Database.CreateContainerIfNotExistsAsync(
            ContainerName,
            "/agentPhoneNumber");
        return containerResponse.Container;
    }

    public async Task CreatePlan(string name, string userPhoneNumber, string agentPhoneNumber, List<StepModel> steps)
    {
        _logger.LogInformation("PlanningClient.CreatePlan: Creating plan '{PlanName}' for user {UserPhoneNumber} and agent {AgentPhoneNumber}", name, userPhoneNumber, agentPhoneNumber);

        var planId = Guid.NewGuid();

        var plan = new
        {
            id = planId,
            name = name,
            userPhoneNumber = userPhoneNumber,
            agentPhoneNumber = agentPhoneNumber,
            steps = steps.Select((step, index) => new
            {
                stepNumber = index + 1,
                description = step.Description,
                isCompleted = false,
                delayHours = step.DelayHours
            }).ToList(),
            history = new List<string>(),
            created = DateTimeOffset.UtcNow,
            //nextNotificationTime = deliveryTime,
            //nextNotificationReceiptId = receiptId,
            //nextNotificationMessageId = messageId
        };

        var container = await GetContainerAsync();
        await container.CreateItemAsync(plan, new PartitionKey(agentPhoneNumber));

        await ScheduleMessageForNextStep(userPhoneNumber, agentPhoneNumber, planId, steps.First().DelayHours);
    }

    public async Task<PlanModel?> GetPlanAsync(Guid planId, string agentPhoneNumber)
    {
        _logger.LogInformation("PlanningClient.GetPlanAsync: Retrieving plan {PlanId} for agent {AgentPhoneNumber}", planId, agentPhoneNumber);

        var container = await GetContainerAsync();

        try
        {
            var response = await container.ReadItemAsync<PlanModel>(planId.ToString(), new PartitionKey(agentPhoneNumber));
            var plan = response.Resource;

            return new PlanModel
            {
                Id = plan.Id,
                Name = plan.Name,
                UserPhoneNumber = plan.UserPhoneNumber,
                AgentPhoneNumber = plan.AgentPhoneNumber,
                Steps = plan.Steps
                    .Select(step => new StepModel
                    {
                        StepNumber = step.StepNumber,
                        Description = step.Description,
                        IsCompleted = step.IsCompleted,
                        DelayHours = step.DelayHours
                    })
                    .ToArray(),
                History = plan.History
            };
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> PlanHasIncompleteSteps(Guid planId, string agentPhoneNumber)
    {
        _logger.LogInformation("PlanningClient.PlanHasIncompleteSteps: Checking plan {PlanId} for agent {AgentPhoneNumber}", planId, agentPhoneNumber);

        var plan = await GetPlanAsync(planId, agentPhoneNumber);
        if (plan is null)
        {
            _logger.LogWarning("Plan {PlanId} not found for agent {AgentPhoneNumber}; treating as no incomplete steps.", planId, agentPhoneNumber);
            return false;
        }

        return plan.Steps.Any(step => !step.IsCompleted);
    }

    public async Task<bool> SavePlanHistoryEntry(Guid planId, string agentPhoneNumber, string entry)
    {
        _logger.LogInformation("PlanningClient.SavePlanHistoryEntry: Appending history for plan {PlanId} and agent {AgentPhoneNumber}", planId, agentPhoneNumber);

        var container = await GetContainerAsync();

        try
        {
            var patchOperations = new List<PatchOperation>
            {
                PatchOperation.Add("/history/-", entry)
            };

            await container.PatchItemAsync<PlanModel>(planId.ToString(), new PartitionKey(agentPhoneNumber), patchOperations);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning("PlanningClient.SavePlanHistoryEntry: Plan {PlanId} not found for agent {AgentPhoneNumber}", planId, agentPhoneNumber);
            return false;
        }
    }

    public async Task<string[]> GetPlanHistory(Guid planId, string agentPhoneNumber)
    {
        _logger.LogInformation("PlanningClient.GetPlanHistory: Retrieving history for plan {PlanId} and agent {AgentPhoneNumber}", planId, agentPhoneNumber);

        var plan = await GetPlanAsync(planId, agentPhoneNumber);
        if (plan is null)
        {
            return [];
        }

        return plan.History;
    }

    public async Task<bool> CompleteStepAsync(Guid planId, string agentPhoneNumber, int stepNumber)
    {
        _logger.LogInformation("PlanningClient.CompleteStepAsync: Completing step {StepNumber} for plan {PlanId} and agent {AgentPhoneNumber}", stepNumber, planId, agentPhoneNumber);

        if (stepNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(stepNumber), "stepNumber must be >= 1");
        }

        var container = await GetContainerAsync();

        try
        {
            var readResponse = await container.ReadItemAsync<PlanModel>(planId.ToString(), new PartitionKey(agentPhoneNumber));
            var plan = readResponse.Resource;

            var stepIndex = Array.FindIndex(plan.Steps, s => s.StepNumber == stepNumber);
            if (stepIndex < 0)
            {
                return false;
            }

            var completedAt = DateTimeOffset.UtcNow;

            var patchOperations = new List<PatchOperation>
            {
                PatchOperation.Set($"/steps/{stepIndex}/completed", completedAt),
                PatchOperation.Set($"/steps/{stepIndex}/isCompleted", true)
            };

            await container.PatchItemAsync<PlanModel>(planId.ToString(), new PartitionKey(agentPhoneNumber), patchOperations);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<(DateTimeOffset deliveryTime, string receiptId, string messageId)> ScheduleMessageForNextStep(string userPhoneNumber, string agentPhoneNumber, Guid planId, decimal delayHours)
    {
        _logger.LogInformation("PlanningClient.SchedulePlanStepExecution: Scheduling execution of plan {PlanId} for user {UserPhoneNumber} and agent {AgentPhoneNumber} with delay {DelayHours} hours", planId, userPhoneNumber, agentPhoneNumber, delayHours);

        var plan = await GetPlanAsync(planId, agentPhoneNumber);
        if (plan is null)
        {
            _logger.LogWarning("Plan {PlanId} not found for agent {AgentPhoneNumber}; skipping schedule.", planId, agentPhoneNumber);
            return default;
        }

        if (plan.Steps.Length > 0 && plan.Steps.All(s => s.IsCompleted))
        {
            _logger.LogWarning("Plan {PlanId} has all steps completed; skipping schedule.", planId);
            return default;
        }

        var queueMessage = new PlanQueueMessage
        {
            PlanId = planId,
            UserPhoneNumber = userPhoneNumber,
            AgentPhoneNumber = agentPhoneNumber
        };

        var queueClient = _queueServiceClient.GetQueueClient(PlanQueueMessage.QueueName);
        await queueClient.CreateIfNotExistsAsync();

        var messageBytes = JsonSerializer.SerializeToUtf8Bytes(queueMessage);
        var base64Message = Convert.ToBase64String(messageBytes);
        var visibilityTimeout = TimeSpan.FromHours((double)delayHours);

        var sendResponse = await queueClient.SendMessageAsync(base64Message, visibilityTimeout: visibilityTimeout);

        var deliveryTime = sendResponse.Value.TimeNextVisible;
        return (deliveryTime, sendResponse.Value.PopReceipt, sendResponse.Value.MessageId);
    }
}

public record PlanQueueMessage
{
    public const string QueueName = "plan-queue";

    public Guid PlanId { get; init; }
    public required string UserPhoneNumber { get; init; }
    public required string AgentPhoneNumber { get; init; }
}
