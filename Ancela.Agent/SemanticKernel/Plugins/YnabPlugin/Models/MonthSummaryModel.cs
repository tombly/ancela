namespace Ancela.Agent.SemanticKernel.Plugins.YnabPlugin.Models;

public class MonthSummaryModel
{
    public DateTimeOffset Month { get; set; }
    public decimal Income { get; set; }
    public decimal Budgeted { get; set; }
    public decimal Activity { get; set; }
    public decimal ReadyToAssign { get; set; }
    public int? AgeOfMoney { get; set; }
}
