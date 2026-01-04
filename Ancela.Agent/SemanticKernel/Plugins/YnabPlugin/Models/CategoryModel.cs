using Ynab.Api.Client;

namespace Ancela.Agent.SemanticKernel.Plugins.YnabPlugin.Models;

public class CategoryModel
{
    public string? CategoryGroupName { get; set; }
    public required string Name { get; set; }
    public decimal Budgeted { get; set; }
    public decimal Activity { get; set; }
    public decimal Balance { get; set; }
    public CategoryGoalType? GoalType { get; set; }
    public decimal? GoalTarget { get; set; }
    public int? GoalPercentageComplete { get; set; }
    public decimal? MonthlyNeed { get; set; }
}
