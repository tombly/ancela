namespace Ancela.Agent.SemanticKernel.Plugins.PlanningPlugin.Models;

public class PlanModel
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string UserPhoneNumber { get; set; }
    public required string AgentPhoneNumber { get; set; }
    public StepModel[] Steps { get; set; } = [];
    public string[] History { get; set; } = [];
}

public class StepModel
{
    public required int StepNumber { get; set; }
    public required string Description { get; set; }
    public required bool IsCompleted { get; set; }
    public required decimal DelayHours { get; set; }
}
