namespace Ancela.Agent.SemanticKernel.Plugins.PlanningPlugin.Models;

public class PlanModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string UserPhoneNumber { get; set; } = string.Empty;
    public string AgentPhoneNumber { get; set; } = string.Empty;
    public StepModel[] Steps { get; set; } = [];
    public string[] History { get; set; } = [];
}

public class StepModel
{
    public int StepNumber { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public decimal DelayHours { get; set; }
}
