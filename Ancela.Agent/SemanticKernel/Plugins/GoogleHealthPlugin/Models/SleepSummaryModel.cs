namespace Ancela.Agent.SemanticKernel.Plugins.GoogleHealthPlugin.Models;

/// <summary>
/// Sleep summary for a single date, from the Google Health API reconciled sleep stream.
/// <see cref="Stages"/> is null when the session wasn't staged (classic sleep); all minute
/// fields are nullable so "unavailable" never collapses to zero.
/// </summary>
public class SleepSummaryModel
{
    /// <summary>The date the sleep is logged under (yyyy-MM-dd).</summary>
    public required string Date { get; set; }

    /// <summary>Total minutes asleep (sum of non-awake stages), or null if unavailable.</summary>
    public int? AsleepMinutes { get; set; }

    /// <summary>Total minutes of the sleep session, or null if unavailable.</summary>
    public int? InBedMinutes { get; set; }

    /// <summary>Sleep efficiency (0-100), or null if unavailable.</summary>
    public int? Efficiency { get; set; }

    /// <summary>Per-stage minutes; null when the session wasn't staged.</summary>
    public SleepStages? Stages { get; set; }
}

public class SleepStages
{
    public int DeepMinutes { get; set; }
    public int LightMinutes { get; set; }
    public int RemMinutes { get; set; }
    public int AwakeMinutes { get; set; }
}
