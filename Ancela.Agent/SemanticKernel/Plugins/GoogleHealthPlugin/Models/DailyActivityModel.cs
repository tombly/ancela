namespace Ancela.Agent.SemanticKernel.Plugins.GoogleHealthPlugin.Models;

/// <summary>
/// Daily activity totals for a single date, assembled from the Google Health API's per-data-type
/// daily roll-ups (steps, distance, calories, active minutes).
/// </summary>
public class DailyActivityModel
{
    /// <summary>The date these totals are for (yyyy-MM-dd).</summary>
    public required string Date { get; set; }

    public int Steps { get; set; }

    /// <summary>Total distance for the day in kilometres (the API reports millimetres).</summary>
    public double DistanceKm { get; set; }

    /// <summary>Total calories burned for the day (kcal).</summary>
    public int CaloriesOut { get; set; }

    /// <summary>Whole minutes at light activity intensity.</summary>
    public int LightActiveMinutes { get; set; }

    /// <summary>Whole minutes at moderate activity intensity.</summary>
    public int ModerateActiveMinutes { get; set; }

    /// <summary>Whole minutes at vigorous activity intensity.</summary>
    public int VigorousActiveMinutes { get; set; }
}
