namespace Ancela.Agent.SemanticKernel.Plugins.FitbitPlugin.Models;

/// <summary>
/// Heart-rate summary for a single date. <see cref="RestingHeartRate"/> is null when Fitbit lacked
/// enough wear data to compute it for that day.
/// </summary>
public class HeartRateModel
{
    /// <summary>The date (yyyy-MM-dd).</summary>
    public required string Date { get; set; }

    /// <summary>Resting heart rate in bpm, or null if unavailable for the date.</summary>
    public int? RestingHeartRate { get; set; }

    /// <summary>Time spent in each heart-rate zone for the date.</summary>
    public HeartRateZone[] Zones { get; set; } = [];
}

public class HeartRateZone
{
    public required string Name { get; set; }
    public int Min { get; set; }
    public int Max { get; set; }
    public int Minutes { get; set; }
}
