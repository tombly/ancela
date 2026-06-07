namespace Ancela.Agent.SemanticKernel.Plugins.FitbitPlugin.Models;

/// <summary>
/// Sleep summary for a single date. <see cref="Stages"/> is null when Fitbit couldn't stage the
/// sleep (classic or very short sleeps), so callers must tolerate its absence.
/// </summary>
public class SleepSummaryModel
{
    /// <summary>The date the sleep is logged under (yyyy-MM-dd).</summary>
    public required string Date { get; set; }

    /// <summary>Total minutes actually asleep across all sleep records for the date.</summary>
    public int AsleepMinutes { get; set; }

    /// <summary>Total minutes in bed across all sleep records for the date.</summary>
    public int InBedMinutes { get; set; }

    /// <summary>Sleep efficiency (0-100) of the main sleep, or null if unavailable.</summary>
    public int? Efficiency { get; set; }

    /// <summary>Per-stage minutes; null when the sleep wasn't staged.</summary>
    public SleepStages? Stages { get; set; }
}

public class SleepStages
{
    public int DeepMinutes { get; set; }
    public int LightMinutes { get; set; }
    public int RemMinutes { get; set; }
    public int AwakeMinutes { get; set; }
}
