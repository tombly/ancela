using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using FluentAssertions;
using Microsoft.SemanticKernel;
using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// Unit tests for finding a particular email: how the model's search text is quoted for Graph's
/// $search, and how get_recent_emails passes its optional arguments to the client. MailSearchLiveTests
/// covers the Graph calls.
/// </summary>
public class EmailSearchTests
{
    [Theory]
    [InlineData("from:someone@example.com", "\"from:someone@example.com\"")]
    [InlineData("  invoice march ", "\"invoice march\"")]
    [InlineData("subject:\"quarterly report\"", "\"subject:\\\"quarterly report\\\"\"")]
    [InlineData(@"a\b", @"""a\\b""")]
    [InlineData(@"a\""b", @"""a\\\""b""")]
    public void SearchClause_QuotesTheWholeQuery_AndEscapesQuotesAndBackslashes(string search, string expected)
    {
        GraphClient.SearchClause(search).Should().Be(expected);
    }

    [Fact]
    public async Task GetRecentEmails_PassesTheSearchThrough()
    {
        var (kernel, graph) = Build();

        await kernel.InvokeAsync(kernel.Plugins["GraphPlugin"]["get_recent_emails"],
            new() { ["search"] = "from:someone@example.com" });

        graph.Verify(g => g.GetUserEmailsAsync(MailFolder.Inbox, "from:someone@example.com"));
    }

    [Fact]
    public void GetRecentEmails_HasNoDateWindow()
    {
        // Removed: the 50-result cap is always the tighter limit, so a window could only hide
        // emails, and the model passed one unasked. A model that still sends one is ignored.
        var (kernel, _) = Build();

        kernel.Plugins["GraphPlugin"]["get_recent_emails"].Metadata.Parameters.Select(p => p.Name)
            .Should().Equal("folder", "search");
    }

    [Fact]
    public async Task GetRecentEmails_WithoutArguments_LeavesTheDefaultsToTheClient()
    {
        var (kernel, graph) = Build();

        await kernel.InvokeAsync(kernel.Plugins["GraphPlugin"]["get_recent_emails"]);

        graph.Verify(g => g.GetUserEmailsAsync(MailFolder.Inbox, null));
    }

    [Fact]
    public async Task GetRecentEmails_BindsTheModelsStringArguments()
    {
        // Tool-call arguments arrive as strings; the enum still has to bind.
        var (kernel, graph) = Build();

        await kernel.InvokeAsync(kernel.Plugins["GraphPlugin"]["get_recent_emails"],
            new() { ["folder"] = "Sent", ["search"] = "invoice" });

        graph.Verify(g => g.GetUserEmailsAsync(MailFolder.Sent, "invoice"));
    }

    private static (Kernel Kernel, Mock<IGraphClient> Graph) Build()
    {
        var graph = new Mock<IGraphClient>();
        graph.Setup(g => g.GetUserEmailsAsync(It.IsAny<MailFolder>(), It.IsAny<string?>()))
            .ReturnsAsync([]);
        var plugins = new KernelPluginCollection();
        plugins.AddFromObject(new GraphPlugin(graph.Object));
        return (new Kernel(plugins: plugins), graph);
    }
}
