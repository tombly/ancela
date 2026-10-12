using Ancela.Agent;
using Ancela.Agent.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.Tests;

/// <summary>
/// Covers what the audit log records for a tool's return value.
///
/// This matters because the audit container is the only record of what a tool actually returned:
/// `check_anomalies` scans it, and it is the sole evidence when the agent reports something wrong
/// (e.g. a bad temperature from web_search). Storing a type name instead of the payload makes
/// those questions unanswerable after the fact.
/// </summary>
public class AuditFilterTests
{
    private const string User = "+15551234567";
    private const string Agent = "+10000000000";

    private sealed record Forecast(string Location, int HighF, int LowF);

    [Fact]
    public async Task ComplexResult_IsRecordedAsJson_NotATypeName()
    {
        var (kernel, audit) = Build(() => new[]
        {
            new Forecast("Mill Creek, WA", 63, 37),
            new Forecast("Seattle, WA", 61, 45),
        });

        await kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["get_forecast"]);

        var result = audit.Entries.Should().ContainSingle().Subject.Result;
        result.Should().NotBeNull();
        result.Should().NotContain("Forecast[]", because: "a type name tells a reader nothing");
        result.Should().Contain("Mill Creek, WA").And.Contain("63").And.Contain("37");
    }

    [Fact]
    public async Task StringResult_IsRecordedVerbatim()
    {
        var (kernel, audit) = Build(() => "Mill Creek high: 63F");

        await kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["get_forecast"]);

        audit.Entries.Should().ContainSingle().Subject.Result.Should().Be("Mill Creek high: 63F");
    }

    [Fact]
    public async Task OverlongResult_IsTruncatedToValidJson()
    {
        var many = Enumerable.Range(0, 400)
            .Select(i => new Forecast($"City {i} with a deliberately long name", i, i - 20))
            .ToArray();
        var (kernel, audit) = Build(() => many);

        await kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["get_forecast"]);

        var result = audit.Entries.Should().ContainSingle().Subject.Result!;
        result.Should().Contain("\"truncated\":true");
        var act = () => System.Text.Json.JsonDocument.Parse(result);
        act.Should().NotThrow(because: "downstream readers parse this as JSON");
    }

    [Fact]
    public async Task FailedCall_RecordsTheErrorAndNoResult()
    {
        var (kernel, audit) = Build(object () => throw new InvalidOperationException("tavily exploded"));

        var act = async () => await kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["get_forecast"]);
        await act.Should().ThrowAsync<Exception>();

        var entry = audit.Entries.Should().ContainSingle().Subject;
        entry.Success.Should().BeFalse();
        entry.Result.Should().BeNull();
        entry.Error.Should().Contain("tavily exploded");
    }

    private static (Kernel Kernel, SpyAuditLog Audit) Build<T>(Func<T> body)
    {
        var audit = new SpyAuditLog();
        var kernel = new Kernel();
        kernel.Data["userPhoneNumber"] = User;
        kernel.Data["agentPhoneNumber"] = Agent;
        kernel.FunctionInvocationFilters.Add(
            new AuditFilter(audit, new CorrelationContext(), NullLogger<AuditFilter>.Instance));

        var fn = KernelFunctionFactory.CreateFromMethod(body, functionName: "get_forecast");
        kernel.Plugins.AddFromFunctions("TestPlugin", [fn]);
        return (kernel, audit);
    }

    private sealed class SpyAuditLog : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task LogAsync(AuditEntry entry)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }
}
