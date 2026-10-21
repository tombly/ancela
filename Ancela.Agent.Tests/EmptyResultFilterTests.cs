using FluentAssertions;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.Tests;

/// <summary>
/// A tool call that worked but found nothing must read differently from one that failed. The
/// filter turns an empty result into an explicit "found nothing" message and leaves values,
/// functions without a return value, and failures alone.
/// </summary>
public class EmptyResultFilterTests
{
    private static async Task<object?> InvokeAsync(Delegate body, string name = "get_things")
    {
        var kernel = new Kernel();
        kernel.FunctionInvocationFilters.Add(new EmptyResultFilter());
        var function = KernelFunctionFactory.CreateFromMethod(body, functionName: name);
        return (await kernel.InvokeAsync(function)).GetValue<object>();
    }

    public static TheoryData<string, Delegate> EmptyResults => new()
    {
        { "null object", () => (string[]?)null },
        { "null from an async method", async () => { await Task.Yield(); return (object?)null; } },
        { "empty string", () => "" },
        { "whitespace", () => "  \n" },
        { "empty array", () => Array.Empty<string>() },
        { "empty list", () => new List<int>() },
        { "empty sequence", () => Enumerable.Empty<int>().Where(_ => true) },
    };

    [Theory]
    [MemberData(nameof(EmptyResults))]
    public async Task EmptyResult_SaysNothingWasFound(string _, Delegate body)
    {
        var result = await InvokeAsync(body, "get_sleep_summary");

        result.Should().BeOfType<string>().Which.Should()
            .Contain("get_sleep_summary").And.Contain(EmptyResultFilter.NothingFoundMarker).And.Contain("not an error");
    }

    public static TheoryData<string, Delegate, object> Values => new()
    {
        { "array", () => new[] { "a" }, new[] { "a" } },
        { "text", () => "hello", "hello" },
        // A count of zero is a measurement (no clients connected), not an absence.
        { "zero", () => 0L, 0L },
        { "false", () => false, false },
    };

    [Theory]
    [MemberData(nameof(Values))]
    public async Task Value_IsUnchanged(string _, Delegate body, object expected)
    {
        var result = await InvokeAsync(body);

        result.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task FunctionWithoutAReturnValue_IsUnchanged()
    {
        var ran = false;
        var result = await InvokeAsync(async () => { await Task.Yield(); ran = true; }, "send_sms");

        ran.Should().BeTrue();
        result.Should().BeNull("send_sms has nothing to find, so there is nothing to report as missing");
    }

    [Fact]
    public async Task Failure_IsNotReportedAsNothingFound()
    {
        static string[] Fails() => throw new HttpRequestException("503 Service Unavailable");
        var act = () => InvokeAsync(Fails);

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("503*");
    }
}
