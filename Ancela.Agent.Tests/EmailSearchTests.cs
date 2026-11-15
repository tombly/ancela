using Ancela.Agent.SemanticKernel.Plugins.ICloudPlugin;
using FluentAssertions;
using Microsoft.SemanticKernel;
using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// Unit tests for finding a particular email: how get_recent_emails passes its optional arguments
/// to the client. ImapMailTests covers the search as IMAP SEARCH, ICloudMailLiveTests the real one.
/// </summary>
public class EmailSearchTests
{
    [Fact]
    public async Task GetRecentEmails_PassesTheSearchThrough()
    {
        var (kernel, graph) = Build();

        await kernel.InvokeAsync(kernel.Plugins["ICloudPlugin"]["get_recent_emails"],
            new() { ["search"] = "from:someone@example.com" });

        graph.Verify(g => g.GetUserEmailsAsync(MailFolder.Inbox, "from:someone@example.com"));
    }

    [Fact]
    public void GetRecentEmails_HasNoDateWindow()
    {
        // Removed: the 50-result cap is always the tighter limit, so a window could only hide
        // emails, and the model passed one unasked. A model that still sends one is ignored.
        var (kernel, _) = Build();

        kernel.Plugins["ICloudPlugin"]["get_recent_emails"].Metadata.Parameters.Select(p => p.Name)
            .Should().Equal("folder", "search");
    }

    [Fact]
    public async Task GetRecentEmails_WithoutArguments_LeavesTheDefaultsToTheClient()
    {
        var (kernel, graph) = Build();

        await kernel.InvokeAsync(kernel.Plugins["ICloudPlugin"]["get_recent_emails"]);

        graph.Verify(g => g.GetUserEmailsAsync(MailFolder.Inbox, null));
    }

    [Fact]
    public async Task GetRecentEmails_BindsTheModelsStringArguments()
    {
        // Tool-call arguments arrive as strings; the enum still has to bind.
        var (kernel, graph) = Build();

        await kernel.InvokeAsync(kernel.Plugins["ICloudPlugin"]["get_recent_emails"],
            new() { ["folder"] = "Sent", ["search"] = "invoice" });

        graph.Verify(g => g.GetUserEmailsAsync(MailFolder.Sent, "invoice"));
    }

    private static (Kernel Kernel, Mock<IOwnerAccounts> Graph) Build()
    {
        var graph = new Mock<IOwnerAccounts>();
        graph.Setup(g => g.GetUserEmailsAsync(It.IsAny<MailFolder>(), It.IsAny<string?>()))
            .ReturnsAsync([]);
        var plugins = new KernelPluginCollection();
        plugins.AddFromObject(new ICloudPlugin(graph.Object, graph.Object, graph.Object));
        return (new Kernel(plugins: plugins), graph);
    }
}
