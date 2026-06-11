using Ancela.Agent.SemanticKernel.Plugins.DiagnosticsPlugin.Models;

namespace Ancela.Agent.SemanticKernel.Plugins.DiagnosticsPlugin;

/// <summary>
/// Turns raw audit activity counts into a deterministic anomaly verdict. Pure threshold logic —
/// no I/O — so what counts as "anomalous" is reproducible and unit-testable rather than left to
/// the model's judgment. The model only relays these findings.
/// </summary>
public static class AnomalyEvaluator
{
    // Volume thresholds are per 24 hours and scale linearly with longer scan windows.
    public const int WebSearchThresholdPer24h = 50;
    public const int SmsSendThresholdPer24h = 20;
    public const int EmailSendThresholdPer24h = 10;

    public static AnomalyReport Evaluate(AuditActivitySummary activity)
    {
        var findings = new List<AnomalyFinding>();

        // --- Security signals: any occurrence is an alert. ---

        if (activity.UnknownSenderMessageCount > 0)
            findings.Add(new AnomalyFinding
            {
                Severity = "alert",
                Title = "Messages from unknown numbers",
                Detail = $"{activity.UnknownSenderMessageCount} dropped message(s) from " +
                         $"{activity.UnknownSenders.Length} unknown number(s): {string.Join(", ", activity.UnknownSenders)}",
            });

        if (activity.FailedStepUpCount > 0)
            findings.Add(new AnomalyFinding
            {
                Severity = "alert",
                Title = "Failed owner step-up attempts",
                Detail = $"{activity.FailedStepUpCount} invite/revoke attempt(s) failed TOTP verification. " +
                         "If these weren't your own typos, someone may be spoofing the owner number.",
            });

        if (activity.GuardDenialCount > 0)
            findings.Add(new AnomalyFinding
            {
                Severity = "alert",
                Title = "Blocked tool calls",
                Detail = $"{activity.GuardDenialCount} invocation(s) were hard-denied by the tool guard " +
                         "(an autonomous run or non-owner attempted a function outside its allow-list).",
            });

        // --- Volume signals: warn when above the (window-scaled) per-24h threshold. ---

        var scale = Math.Max(1.0, activity.WindowHours / 24.0);
        AddVolumeFinding(findings, "web_search", activity.WebSearchCount, WebSearchThresholdPer24h, scale);
        AddVolumeFinding(findings, "send_sms", activity.SmsSendCount, SmsSendThresholdPer24h, scale);
        AddVolumeFinding(findings, "send_email", activity.EmailSendCount, EmailSendThresholdPer24h, scale);

        // --- Error signals. ---

        if (activity.FailedFunctions.Length > 0)
        {
            var top = activity.FailedFunctions.Take(3)
                .Select(f => $"{f.Plugin}.{f.Function} ×{f.Count}");
            var latest = activity.RecentFailures.FirstOrDefault();
            var latestNote = latest is null ? "" : $" Most recent: {latest.Function} — {latest.Error}";
            findings.Add(new AnomalyFinding
            {
                Severity = "warn",
                Title = "Tool errors",
                Detail = $"{activity.FailedFunctions.Sum(f => f.Count)} failed call(s): " +
                         $"{string.Join(", ", top)}.{latestNote}",
            });
        }

        // --- Informational: access changes are worth surfacing but not anomalous by themselves. ---

        var accessChanges = new List<string>();
        if (activity.InviteCount > 0) accessChanges.Add($"{activity.InviteCount} invite(s)");
        if (activity.RevokeCount > 0) accessChanges.Add($"{activity.RevokeCount} revoke(s)");
        if (activity.DeregisterCount > 0) accessChanges.Add($"{activity.DeregisterCount} deregistration(s)");
        if (accessChanges.Count > 0)
            findings.Add(new AnomalyFinding
            {
                Severity = "info",
                Title = "Access changes",
                Detail = string.Join(", ", accessChanges) + " in the window.",
            });

        return new AnomalyReport
        {
            WindowHours = activity.WindowHours,
            Verdict = findings.Any(f => f.Severity is "alert" or "warn") ? "review" : "all-clear",
            Findings = [.. findings],
            Activity = activity,
        };
    }

    private static void AddVolumeFinding(List<AnomalyFinding> findings, string function, int count, int thresholdPer24h, double scale)
    {
        var threshold = (int)(thresholdPer24h * scale);
        if (count <= threshold)
            return;

        findings.Add(new AnomalyFinding
        {
            Severity = "warn",
            Title = $"High {function} volume",
            Detail = $"{count} {function} call(s) in the window (threshold {threshold}). " +
                     "Check for a runaway standing rule or scheduled task.",
        });
    }
}
