namespace Ancela.Agent.SemanticKernel.Plugins.DiagnosticsPlugin.Models;

/// <summary>The outcome of probing one external service during a self-check.</summary>
public class ServiceProbeResult
{
    public required string Service { get; init; }

    /// <summary>"ok", "degraded" (reachable but something needs attention), or "fail".</summary>
    public required string Status { get; init; }

    public long LatencyMs { get; init; }

    /// <summary>What was verified, or what went wrong.</summary>
    public string? Detail { get; init; }
}
