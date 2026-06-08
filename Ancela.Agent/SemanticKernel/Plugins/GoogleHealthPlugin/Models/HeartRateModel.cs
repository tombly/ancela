namespace Ancela.Agent.SemanticKernel.Plugins.GoogleHealthPlugin.Models;

/// <summary>
/// Daily resting heart rate for a single date. All fields nullable: the API reports nothing for
/// days without enough wear data.
/// </summary>
public class HeartRateModel
{
    /// <summary>The date (yyyy-MM-dd).</summary>
    public required string Date { get; set; }

    /// <summary>Resting heart rate in bpm (daily average), or null if unavailable.</summary>
    public int? RestingHeartRate { get; set; }

    /// <summary>Minimum resting heart rate in the personal range (bpm), or null.</summary>
    public int? Min { get; set; }

    /// <summary>Maximum resting heart rate in the personal range (bpm), or null.</summary>
    public int? Max { get; set; }
}
