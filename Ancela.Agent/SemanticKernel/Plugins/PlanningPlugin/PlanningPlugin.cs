using System.ComponentModel;
using Ancela.Agent.SemanticKernel.Plugins.PlanningPlugin.Models;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.SemanticKernel.Plugins.PlanningPlugin;

/// <summary>
/// Semantic Kernel plugin for planning tasks and managing plans.
/// </summary>
public class PlanningPlugin(IPlanningClient _planningClient)
{
    [KernelFunction("create_plan")]
    [Description("Creates a new plan and schedules a message to be sent to the agent when the first step is ready to be executed.")]
    public async Task CreatePlanAsync(Kernel kernel,
        [Description("A short name for the plan")] string name,
        [Description("An ordered list of steps to complete the plan. Each step includes a description of what the agent should do, and how long to wait until executing the step after the previous step is completed.")] List<StepModel> steps)
    {
        EnsureKernelData(kernel);
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString()!;
        var userPhoneNumber = kernel.Data["userPhoneNumber"]?.ToString()!;

        await _planningClient.CreatePlan(name, userPhoneNumber, agentPhoneNumber, steps);
    }

    [KernelFunction("get_plan")]
    [Description("Gets a plan by id for the current agent.")]
    public Task<PlanModel?> GetPlanAsync(Kernel kernel,
        [Description("The plan id (GUID).")] string planId)
    {
        EnsureKernelData(kernel);
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString()!;

        if (!Guid.TryParse(planId, out var parsedPlanId))
            throw new ArgumentException("planId must be a valid GUID", nameof(planId));

        return _planningClient.GetPlanAsync(parsedPlanId, agentPhoneNumber);
    }

    [KernelFunction("schedule_message_for_next_step")]
    [Description("Schedules a message to be sent to the agent when the next step in the plan is ready to execute.")]
    public async Task ScheduleMessageForNextStep(Kernel kernel,
        [Description("The plan id (GUID). ")] string planId,
        [Description("How long until the next step should be executed, in hours.")] decimal delayHours)
    {
        EnsureKernelData(kernel);
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString()!;
        var userPhoneNumber = kernel.Data["userPhoneNumber"]?.ToString()!;

        if (!Guid.TryParse(planId, out var parsedPlanId))
            throw new ArgumentException("planId must be a valid GUID", nameof(planId));

        await _planningClient.ScheduleMessageForNextStep(userPhoneNumber, agentPhoneNumber, parsedPlanId, delayHours);
    }

    [KernelFunction("plan_has_incomplete_steps")]
    [Description("Checks whether the plan has any incomplete steps.")]
    public Task<bool> PlanHasIncompleteStepsAsync(Kernel kernel,
        [Description("The plan id (GUID). ")] string planId)
    {
        EnsureKernelData(kernel);
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString()!;

        if (!Guid.TryParse(planId, out var parsedPlanId))
            throw new ArgumentException("planId must be a valid GUID", nameof(planId));

        return _planningClient.PlanHasIncompleteSteps(parsedPlanId, agentPhoneNumber);
    }

    [KernelFunction("complete_plan_step")]
    [Description("Marks a plan step as completed using the step number (1-based).")]
    public Task<bool> CompletePlanStepAsync(Kernel kernel,
        [Description("The plan id (GUID). ")] string planId,
        [Description("The step number (1-based) to mark complete.")] int stepNumber)
    {
        EnsureKernelData(kernel);
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString()!;

        if (!Guid.TryParse(planId, out var parsedPlanId))
            throw new ArgumentException("planId must be a valid GUID", nameof(planId));

        return _planningClient.CompleteStepAsync(parsedPlanId, agentPhoneNumber, stepNumber);
    }

    /// <summary>
    /// Adds a model response entry to the plan history (non-kernel helper).
    /// </summary>
    public Task<bool> SaveToPlanHistory(Guid planId, string agentPhoneNumber, string entry)
    {
        return _planningClient.SavePlanHistoryEntry(planId, agentPhoneNumber, entry);
    }

    /// <summary>
    /// Retrieves the plan history entries (non-kernel helper).
    /// </summary>
    public Task<string[]> GetPlanHistory(Guid planId, string agentPhoneNumber)
    {
        return _planningClient.GetPlanHistory(planId, agentPhoneNumber);
    }

    private static void EnsureKernelData(Kernel kernel)
    {
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString();
        var userPhoneNumber = kernel.Data["userPhoneNumber"]?.ToString();

        if (string.IsNullOrWhiteSpace(agentPhoneNumber))
        {
            throw new InvalidOperationException("agentPhoneNumber is required in kernel data");
        }

        if (string.IsNullOrWhiteSpace(userPhoneNumber))
        {
            throw new InvalidOperationException("userPhoneNumber is required in kernel data");
        }
    }
}
