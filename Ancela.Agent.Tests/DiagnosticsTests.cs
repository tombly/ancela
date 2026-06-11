using Ancela.Agent.SemanticKernel.Plugins.DiagnosticsPlugin;
using Ancela.Agent.SemanticKernel.Plugins.DiagnosticsPlugin.Models;
using FluentAssertions;
using Microsoft.SemanticKernel;
using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// Tests for the diagnostics self-check: the deterministic anomaly threshold logic
/// (<see cref="AnomalyEvaluator"/>) and the plugin's argument handling. No live services
/// or OpenAI key required.
/// </summary>
public class DiagnosticsTests
{
    private static AuditActivitySummary Quiet(int windowHours = 24) => new() { WindowHours = windowHours };

    // --- Verdict ---

    [Fact]
    public void QuietWindow_IsAllClear_WithNoFindings()
    {
        var report = AnomalyEvaluator.Evaluate(Quiet());

        report.Verdict.Should().Be("all-clear");
        report.Findings.Should().BeEmpty();
        report.WindowHours.Should().Be(24);
    }

    [Fact]
    public void InfoOnlyFindings_DoNotFlipVerdictToReview()
    {
        // Access changes are surfaced but aren't anomalous by themselves.
        var report = AnomalyEvaluator.Evaluate(new AuditActivitySummary
        {
            WindowHours = 24,
            InviteCount = 1,
            RevokeCount = 1,
        });

        report.Verdict.Should().Be("all-clear");
        report.Findings.Should().OnlyContain(f => f.Severity == "info");
        report.Findings.Single().Detail.Should().Contain("1 invite(s)").And.Contain("1 revoke(s)");
    }

    // --- Security signals: any occurrence is an alert ---

    [Fact]
    public void UnknownSenders_ProduceAlert()
    {
        var report = AnomalyEvaluator.Evaluate(new AuditActivitySummary
        {
            WindowHours = 24,
            UnknownSenderMessageCount = 3,
            UnknownSenders = ["+15550000001", "+15550000002"],
        });

        report.Verdict.Should().Be("review");
        var finding = report.Findings.Single(f => f.Title == "Messages from unknown numbers");
        finding.Severity.Should().Be("alert");
        finding.Detail.Should().Contain("+15550000001").And.Contain("+15550000002");
    }

    [Fact]
    public void FailedStepUps_ProduceAlert()
    {
        var report = AnomalyEvaluator.Evaluate(new AuditActivitySummary
        {
            WindowHours = 24,
            FailedStepUpCount = 1,
        });

        report.Verdict.Should().Be("review");
        report.Findings.Single().Severity.Should().Be("alert");
        report.Findings.Single().Title.Should().Be("Failed owner step-up attempts");
    }

    [Fact]
    public void GuardDenials_ProduceAlert()
    {
        var report = AnomalyEvaluator.Evaluate(new AuditActivitySummary
        {
            WindowHours = 24,
            GuardDenialCount = 2,
        });

        report.Verdict.Should().Be("review");
        report.Findings.Single().Severity.Should().Be("alert");
        report.Findings.Single().Title.Should().Be("Blocked tool calls");
    }

    // --- Volume signals: warn only above the window-scaled per-24h threshold ---

    [Theory]
    [InlineData(AnomalyEvaluator.WebSearchThresholdPer24h, false)]      // at threshold — fine
    [InlineData(AnomalyEvaluator.WebSearchThresholdPer24h + 1, true)]   // above — warn
    public void WebSearchVolume_WarnsOnlyAboveThreshold(int count, bool expectWarning)
    {
        var report = AnomalyEvaluator.Evaluate(new AuditActivitySummary
        {
            WindowHours = 24,
            WebSearchCount = count,
        });

        report.Findings.Any(f => f.Title == "High web_search volume").Should().Be(expectWarning);
        report.Verdict.Should().Be(expectWarning ? "review" : "all-clear");
    }

    [Fact]
    public void VolumeThresholds_ScaleWithLongerWindows()
    {
        // 48 hours at just over the 24h threshold is normal traffic, not an anomaly.
        var report = AnomalyEvaluator.Evaluate(new AuditActivitySummary
        {
            WindowHours = 48,
            WebSearchCount = AnomalyEvaluator.WebSearchThresholdPer24h + 1,
        });

        report.Verdict.Should().Be("all-clear");
    }

    [Fact]
    public void SmsAndEmailVolume_WarnAboveThreshold()
    {
        var report = AnomalyEvaluator.Evaluate(new AuditActivitySummary
        {
            WindowHours = 24,
            SmsSendCount = AnomalyEvaluator.SmsSendThresholdPer24h + 1,
            EmailSendCount = AnomalyEvaluator.EmailSendThresholdPer24h + 1,
        });

        report.Verdict.Should().Be("review");
        report.Findings.Should().Contain(f => f.Title == "High send_sms volume")
            .And.Contain(f => f.Title == "High send_email volume");
    }

    // --- Error signals ---

    [Fact]
    public void FailedToolCalls_ProduceWarn_WithGroupsAndLatestError()
    {
        var report = AnomalyEvaluator.Evaluate(new AuditActivitySummary
        {
            WindowHours = 24,
            FailedFunctions =
            [
                new FunctionFailureGroup { Plugin = "GoogleHealthPlugin", Function = "get_sleep_summary", Count = 4 },
                new FunctionFailureGroup { Plugin = "WebPlugin", Function = "web_fetch", Count = 1 },
            ],
            RecentFailures =
            [
                new RecentFailure
                {
                    Plugin = "GoogleHealthPlugin",
                    Function = "get_sleep_summary",
                    Error = "Google Health access has expired",
                    Timestamp = DateTimeOffset.UtcNow,
                },
            ],
        });

        report.Verdict.Should().Be("review");
        var finding = report.Findings.Single(f => f.Title == "Tool errors");
        finding.Severity.Should().Be("warn");
        finding.Detail.Should().Contain("5 failed call(s)");
        finding.Detail.Should().Contain("GoogleHealthPlugin.get_sleep_summary ×4");
        finding.Detail.Should().Contain("Google Health access has expired");
    }

    // --- Plugin argument handling ---

    [Fact]
    public async Task CheckAnomalies_ClampsWindow_AndPassesAgentNumber()
    {
        var scanner = new Mock<IAuditAnomalyScanner>();
        scanner
            .Setup(s => s.ScanAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync((string _, int hours) => Quiet(hours));
        var plugin = new DiagnosticsPlugin(
            new Mock<IServiceHealthChecker>().Object, scanner.Object);

        var kernel = new Kernel();
        kernel.Data["agentPhoneNumber"] = "+15551234567";

        var report = await plugin.CheckAnomaliesAsync(kernel, hours: 99999);

        report.WindowHours.Should().Be(720, because: "the scan window is capped at 30 days");
        scanner.Verify(s => s.ScanAsync("+15551234567", 720), Times.Once);
    }

    [Fact]
    public async Task CheckAnomalies_RequiresAgentIdentityInKernelData()
    {
        var plugin = new DiagnosticsPlugin(
            new Mock<IServiceHealthChecker>().Object,
            new Mock<IAuditAnomalyScanner>().Object);

        var act = async () => await plugin.CheckAnomaliesAsync(new Kernel());

        await act.Should().ThrowAsync<InvalidOperationException>(
            because: "a self-check without per-request identity must fail rather than scan nothing");
    }
}
