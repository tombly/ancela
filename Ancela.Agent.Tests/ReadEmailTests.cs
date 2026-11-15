using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using FluentAssertions;
using Microsoft.SemanticKernel;
using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// Unit tests for read_email and the changes to one email: how a body becomes what the model reads
/// (<see cref="MailText"/>), and that the tools pass the id through. ICloudMailLiveTests covers the
/// IMAP calls.
/// </summary>
public class ReadEmailTests
{
    [Fact]
    public void TidyBody_KeepsTheWholeBody_NotJustThePreview()
    {
        var steps = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"Step {i}: do the {i}th thing to finish setting up."));

        var (body, truncated) = MailText.TidyBody(steps);

        steps.Length.Should().BeGreaterThan(255, because: "this is longer than get_recent_emails' preview");
        body.Should().Be(steps);
        truncated.Should().BeFalse();
    }

    [Fact]
    public void TidyBody_TidiesTextConvertedFromHtml()
    {
        MailText.TidyBody("\r\n  Hello  \r\n\r\n\r\n\r\nStep 1: sign in \t\r\n\r\nStep 2: verify\r\n\r\n").Body
            .Should().Be("Hello\n\nStep 1: sign in\n\nStep 2: verify");
    }

    [Fact]
    public void TidyBody_LongBody_IsCut_AndSaysSo()
    {
        var (body, truncated) = MailText.TidyBody(new string('x', MailText.BodyCap + 500));

        body.Length.Should().Be(MailText.BodyCap);
        truncated.Should().BeTrue(because: "the model must know it hasn't seen the whole email");
    }

    [Fact]
    public void TidyBody_DoesNotCutAnEmojiInHalf()
    {
        var (body, truncated) = MailText.TidyBody(new string('x', MailText.BodyCap - 1) + "😀" + "tail");

        body.Should().Be(new string('x', MailText.BodyCap - 1));
        truncated.Should().BeTrue();
    }

    [Fact]
    public void TidyBody_EmptyBody_IsNoText()
    {
        MailText.TidyBody("  \r\n ").Should().Be((string.Empty, false));
        MailText.TidyBody(null).Should().Be((string.Empty, false));
    }

    [Fact]
    public void Changed_NamesTheEmail_SinceAMoveChangesItsId()
    {
        MailText.Changed("Report domain: example.com Submitter: google.com", "noreply-dmarc-support@google.com", "Moved to Deleted Items")
            .Should().Be("Moved to Deleted Items: \"Report domain: example.com Submitter: google.com\" from noreply-dmarc-support@google.com.");
        MailText.Changed(null, null, "Moved to Deleted Items").Should().Be(
            "Moved to Deleted Items: \"(no subject)\" from an unknown sender.");
    }

    [Fact]
    public async Task TrashEmail_PassesTheIdThrough()
    {
        var graph = new Mock<IOwnerAccounts>();
        graph.Setup(g => g.MoveToDeletedItemsAsync("email-1")).ReturnsAsync("Moved to Deleted Items.");
        var kernel = new Kernel();
        kernel.Plugins.AddFromObject(new GraphPlugin(graph.Object, graph.Object, graph.Object));

        var result = await kernel.InvokeAsync(kernel.Plugins["GraphPlugin"]["trash_email"], new() { ["emailId"] = "email-1" });

        result.GetValue<string>().Should().Be("Moved to Deleted Items.");
        graph.Verify(g => g.MoveToDeletedItemsAsync("email-1"), Times.Once);
    }

    [Theory]
    [InlineData(null, true)]  // "mark it read" needs no second argument
    [InlineData(false, false)]
    public async Task MarkEmailRead_PassesTheIdAndTheFlagThrough(bool? read, bool expected)
    {
        var graph = new Mock<IOwnerAccounts>();
        graph.Setup(g => g.MarkReadAsync("email-1", expected)).ReturnsAsync("Marked.");
        var kernel = new Kernel();
        kernel.Plugins.AddFromObject(new GraphPlugin(graph.Object, graph.Object, graph.Object));
        var arguments = new KernelArguments { ["emailId"] = "email-1" };
        if (read is { } flag)
            arguments["read"] = flag;

        var result = await kernel.InvokeAsync(kernel.Plugins["GraphPlugin"]["mark_email_read"], arguments);

        result.GetValue<string>().Should().Be("Marked.");
        graph.Verify(g => g.MarkReadAsync("email-1", expected), Times.Once);
    }

    [Fact]
    public async Task ReadEmail_PassesTheIdThrough()
    {
        var graph = new Mock<IOwnerAccounts>();
        graph.Setup(g => g.GetEmailAsync("email-1")).ReturnsAsync(new EmailContentModel { Id = "email-1", Subject = "Finish setting up your account", From = "support@example.com", To = ["owner@example.com"], Cc = [], ReceivedDateTime = DateTimeOffset.UtcNow, Body = "Step 1", Truncated = false, Attachments = [] });
        var kernel = new Kernel();
        kernel.Plugins.AddFromObject(new GraphPlugin(graph.Object, graph.Object, graph.Object));

        var result = await kernel.InvokeAsync(kernel.Plugins["GraphPlugin"]["read_email"], new() { ["emailId"] = "email-1" });

        result.GetValue<EmailContentModel>()!.Body.Should().Be("Step 1");
        graph.Verify(g => g.GetEmailAsync("email-1"), Times.Once);
    }
}
