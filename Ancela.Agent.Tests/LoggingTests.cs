using Ancela.FunctionApp;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace Ancela.Agent.Tests;

/// <summary>
/// Pins that the app's logs don't carry what users wrote or tools read. Console output is kept in
/// Log Analytics, and until 2026-11 the app logged at Trace, where Semantic Kernel writes every
/// tool call's arguments and result.
/// </summary>
public class LoggingTests
{
    [Fact]
    public void ToolArgumentsAndResults_AreNotLogged()
    {
        using var factory = LoggerFactory.Create(logging => logging.AddAncelaLogging());

        factory.CreateLogger("Microsoft.SemanticKernel.KernelFunction").IsEnabled(LogLevel.Trace)
            .Should().BeFalse("Semantic Kernel logs a tool call's arguments and result at Trace");
    }

    [Fact]
    public void RequestUrls_AreNotLogged()
    {
        using var factory = LoggerFactory.Create(logging => logging.AddAncelaLogging());

        factory.CreateLogger("System.Net.Http.HttpClient.images.LogicalHandler").IsEnabled(LogLevel.Information)
            .Should().BeFalse("a URL can come from an email, such as a linked image's");
        factory.CreateLogger("System.Net.Http.HttpClient.images.LogicalHandler").IsEnabled(LogLevel.Warning)
            .Should().BeTrue();
    }

    [Fact]
    public void AppLogs_StillReachInformation()
    {
        using var factory = LoggerFactory.Create(logging => logging.AddAncelaLogging());

        factory.CreateLogger<ChatQueueProcessor>().IsEnabled(LogLevel.Information).Should().BeTrue();
    }
}
