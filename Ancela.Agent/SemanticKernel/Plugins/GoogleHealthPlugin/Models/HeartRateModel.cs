namespace Ancela.Agent.SemanticKernel.Plugins.GoogleHealthPlugin.Models;

/// <summary>
/// Daily resting heart rate for a single date. The API reports a single bpm value per day (and
/// nothing on days without enough wear data), so <see cref="RestingHeartRate"/> is nullable.
/// </summary>
public class HeartRateModel
{
    /// <summary>The date (yyyy-MM-dd).</summary>
    public required string Date { get; set; }

    /// <summary>Resting heart rate in bpm for the day, or null if unavailable.</summary>
    public int? RestingHeartRate { get; set; }
}
