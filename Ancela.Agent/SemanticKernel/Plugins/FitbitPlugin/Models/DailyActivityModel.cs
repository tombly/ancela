namespace Ancela.Agent.SemanticKernel.Plugins.FitbitPlugin.Models;

/// <summary>
/// Daily activity totals for a single date, from Fitbit's pre-aggregated activity summary.
/// </summary>
public class DailyActivityModel
{
    /// <summary>The date these totals are for (yyyy-MM-dd).</summary>
    public required string Date { get; set; }

    public int Steps { get; set; }

    /// <summary>The daily step goal, if one is set.</summary>
    public int? StepsGoal { get; set; }

    /// <summary>
    /// Total distance for the day, in the account's distance unit (kilometres unless the Fitbit
    /// account is set to miles).
    /// </summary>
    public double Distance { get; set; }

    /// <summary>Total calories burned for the day.</summary>
    public int CaloriesOut { get; set; }

    public int VeryActiveMinutes { get; set; }
    public int FairlyActiveMinutes { get; set; }
    public int LightlyActiveMinutes { get; set; }
    public int SedentaryMinutes { get; set; }
}
