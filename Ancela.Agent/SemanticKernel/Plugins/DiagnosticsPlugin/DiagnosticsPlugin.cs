using System.ComponentModel;
using Ancela.Agent.SemanticKernel.Plugins.DiagnosticsPlugin.Models;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.SemanticKernel.Plugins.DiagnosticsPlugin;

/// <summary>
/// Owner-only self-check functions: on-demand service reachability probes and an audit-log
/// anomaly scan. Both names are in <see cref="KernelProfilePolicy"/>'s owner-only set and in
/// neither autonomous allow-list, so they are owner-initiated Chat-profile tools only.
/// </summary>
public class DiagnosticsPlugin(IServiceHealthChecker _healthChecker, IAuditAnomalyScanner _anomalyScanner)
{
    private const int MaxWindowHours = 720; // 30 days

    [KernelFunction("check_services")]
    [Description("Self-check (owner only): probes every connected service — Cosmos DB, Service Bus " +
        "dead-letter queues, Twilio, Microsoft Graph, YNAB, Tavily web search, Google Health, and " +
        "reMarkable — and reports per-service status, latency, and any failures. Use when the owner " +
        "asks how you are, whether everything is working, or to verify a deployment.")]
    public async Task<ServiceProbeResult[]> CheckServicesAsync()
    {
        return await _healthChecker.CheckAllAsync();
    }

    [KernelFunction("check_anomalies")]
    [Description("Self-check (owner only): scans the audit log for anomalous activity — messages from " +
        "unknown numbers, failed owner step-up (TOTP) attempts, blocked tool calls, unusually high " +
        "web-search/SMS/email volume, tool errors, and access changes. Returns a deterministic " +
        "verdict ('all-clear' or 'review') with findings. Use when the owner asks how you are, " +
        "whether anything looks wrong, or about suspicious or unusual activity.")]
    public async Task<AnomalyReport> CheckAnomaliesAsync(Kernel kernel,
        [Description("How many hours back to scan (default 24, max 720).")]
        int hours = 24)
    {
        kernel.Data.TryGetValue("agentPhoneNumber", out var agent);
        var agentPhoneNumber = agent?.ToString();
        if (string.IsNullOrWhiteSpace(agentPhoneNumber))
            throw new InvalidOperationException("agentPhoneNumber is required in kernel data");

        var window = Math.Clamp(hours, 1, MaxWindowHours);
        var activity = await _anomalyScanner.ScanAsync(agentPhoneNumber, window);
        return AnomalyEvaluator.Evaluate(activity);
    }
}
