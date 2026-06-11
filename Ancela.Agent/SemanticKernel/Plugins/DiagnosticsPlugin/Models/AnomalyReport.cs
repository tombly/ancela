namespace Ancela.Agent.SemanticKernel.Plugins.DiagnosticsPlugin.Models;

/// <summary>The result of an anomaly scan: a deterministic verdict plus the evidence behind it.</summary>
public class AnomalyReport
{
    public required int WindowHours { get; init; }

    /// <summary>"all-clear" when nothing warrants attention, otherwise "review".</summary>
    public required string Verdict { get; init; }

    public AnomalyFinding[] Findings { get; init; } = [];

    /// <summary>The raw counts the findings were derived from.</summary>
    public required AuditActivitySummary Activity { get; init; }
}

public class AnomalyFinding
{
    /// <summary>"alert" (security-relevant), "warn" (errors or unusual volume), or "info".</summary>
    public required string Severity { get; init; }

    public required string Title { get; init; }

    public required string Detail { get; init; }
}
