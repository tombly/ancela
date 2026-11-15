using System.Text.Json;
using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using FluentAssertions;
using Microsoft.SemanticKernel;
using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// On the Responses API a tool's arguments reach the kernel as the JSON values the model wrote,
/// and Semantic Kernel can't convert a JSON string to an enum: from 2026-11-05 every
/// get_recent_emails call that named its folder failed with "Object of type
/// 'System.Text.Json.JsonElement' cannot be converted to type '…MailFolder'". Chat completions
/// handed tools strings, which convert to any parameter type; the filter does the same.
/// </summary>
public class ToolArgumentFilterTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static (Kernel Kernel, Mock<IMailClient> Mail) GraphKernel(bool withFilter)
    {
        var mail = new Mock<IMailClient>();
        mail.Setup(m => m.GetUserEmailsAsync(It.IsAny<MailFolder>(), It.IsAny<string?>())).ReturnsAsync([]);
        var plugins = new KernelPluginCollection();
        plugins.AddFromObject(new GraphPlugin(Mock.Of<ICalendarClient>(), mail.Object, Mock.Of<IContactsClient>()));
        var kernel = new Kernel(plugins: plugins);
        if (withFilter)
            kernel.FunctionInvocationFilters.Add(new ToolArgumentFilter());
        return (kernel, mail);
    }

    [Fact]
    public async Task WithoutTheFilter_AJsonFolderFails_AsInProduction()
    {
        var (kernel, _) = GraphKernel(withFilter: false);

        var call = () => kernel.InvokeAsync("GraphPlugin", "get_recent_emails",
            new KernelArguments { ["folder"] = Json("\"Inbox\"") });

        (await call.Should().ThrowAsync<Exception>()).Which.ToString().Should().Contain("MailFolder");
    }

    [Theory]
    [InlineData("\"Inbox\"", MailFolder.Inbox)]
    [InlineData("\"Sent\"", MailFolder.Sent)]
    [InlineData("\"deleted\"", MailFolder.Deleted)]
    public async Task AJsonFolder_ReachesTheMailClient(string folder, MailFolder expected)
    {
        var (kernel, mail) = GraphKernel(withFilter: true);

        await kernel.InvokeAsync("GraphPlugin", "get_recent_emails",
            new KernelArguments { ["folder"] = Json(folder), ["search"] = Json("\"invoice march\"") });

        mail.Verify(m => m.GetUserEmailsAsync(expected, "invoice march"), Times.Once);
    }

    [Fact]
    public async Task AJsonNull_LeavesTheParameterAtItsDefault()
    {
        var (kernel, mail) = GraphKernel(withFilter: true);

        await kernel.InvokeAsync("GraphPlugin", "get_recent_emails",
            new KernelArguments { ["folder"] = Json("null"), ["search"] = Json("null") });

        mail.Verify(m => m.GetUserEmailsAsync(MailFolder.Inbox, null), Times.Once);
    }

    [Fact]
    public async Task JsonNumbersAndBooleans_StillConvert()
    {
        int? count = null;
        bool? flag = null;
        var kernel = new Kernel();
        kernel.FunctionInvocationFilters.Add(new ToolArgumentFilter());
        var function = KernelFunctionFactory.CreateFromMethod(
            (int hours, bool? includeLinks) => { count = hours; flag = includeLinks; }, functionName: "check");

        await kernel.InvokeAsync(function, new KernelArguments { ["hours"] = Json("48"), ["includeLinks"] = Json("true") });

        count.Should().Be(48);
        flag.Should().BeTrue();
    }

    [Fact]
    public async Task ArgumentsThatAreNotJson_AreLeftAlone()
    {
        object? seen = null;
        var kernel = new Kernel();
        kernel.FunctionInvocationFilters.Add(new ToolArgumentFilter());
        var function = KernelFunctionFactory.CreateFromMethod((string text) => { seen = text; }, functionName: "echo");

        await kernel.InvokeAsync(function, new KernelArguments { ["text"] = "plain" });

        seen.Should().Be("plain");
    }
}
