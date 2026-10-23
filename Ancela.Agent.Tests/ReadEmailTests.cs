using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using FluentAssertions;
using Microsoft.Graph.Models;
using Microsoft.SemanticKernel;
using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// Unit tests for read_email: how a message fetched with a text body becomes what the model reads,
/// and that the tool passes the id through. MailSearchLiveTests covers the Graph calls.
/// </summary>
public class ReadEmailTests
{
    private static Message Message(string body, bool? hasAttachments = false) => new()
    {
        Id = "email-1",
        Subject = "Finish setting up your account",
        From = new Recipient { EmailAddress = new EmailAddress { Address = "support@example.com" } },
        ToRecipients = [new Recipient { EmailAddress = new EmailAddress { Name = "Owner", Address = "owner@example.com" } }],
        CcRecipients = [new Recipient { EmailAddress = new EmailAddress { Name = "No address" } }],
        ReceivedDateTime = new DateTimeOffset(2026, 10, 20, 15, 0, 0, TimeSpan.Zero),
        Body = new ItemBody { ContentType = BodyType.Text, Content = body },
        HasAttachments = hasAttachments,
    };

    [Fact]
    public void ToEmailContent_KeepsTheWholeBody_NotJustThePreview()
    {
        var steps = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"Step {i}: do the {i}th thing to finish setting up."));

        var email = GraphClient.ToEmailContent(Message(steps, hasAttachments: true));

        steps.Length.Should().BeGreaterThan(255, because: "this is longer than get_recent_emails' bodyPreview");
        email.Body.Should().Be(steps);
        email.Truncated.Should().BeFalse();
        email.HasAttachments.Should().BeTrue();
        email.From.Should().Be("support@example.com");
        email.To.Should().Equal("owner@example.com");
        email.Cc.Should().BeEmpty(because: "a recipient without an address has nothing to show");
    }

    [Fact]
    public void ToEmailContent_TidiesTextConvertedFromHtml()
    {
        var email = GraphClient.ToEmailContent(Message("\r\n  Hello  \r\n\r\n\r\n\r\nStep 1: sign in \t\r\n\r\nStep 2: verify\r\n\r\n"));

        email.Body.Should().Be("Hello\n\nStep 1: sign in\n\nStep 2: verify");
    }

    [Fact]
    public void ToEmailContent_LongBody_IsCut_AndSaysSo()
    {
        var email = GraphClient.ToEmailContent(Message(new string('x', GraphClient.EmailBodyCap + 500)));

        email.Body.Length.Should().Be(GraphClient.EmailBodyCap);
        email.Truncated.Should().BeTrue(because: "the model must know it hasn't seen the whole email");
    }

    [Fact]
    public void ToEmailContent_DoesNotCutAnEmojiInHalf()
    {
        var body = new string('x', GraphClient.EmailBodyCap - 1) + "😀" + "tail";

        var email = GraphClient.ToEmailContent(Message(body));

        email.Body.Should().Be(new string('x', GraphClient.EmailBodyCap - 1));
        email.Truncated.Should().BeTrue();
    }

    [Fact]
    public void ToEmailContent_EmptyBody_IsAnEmailWithNoText()
    {
        var email = GraphClient.ToEmailContent(Message("  \r\n "));

        email.Body.Should().BeEmpty();
        email.Truncated.Should().BeFalse();
        email.Subject.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ReadEmail_PassesTheIdThrough()
    {
        var graph = new Mock<IGraphClient>();
        graph.Setup(g => g.GetEmailAsync("email-1")).ReturnsAsync(GraphClient.ToEmailContent(Message("Step 1")));
        var kernel = new Kernel();
        kernel.Plugins.AddFromObject(new GraphPlugin(graph.Object));

        var result = await kernel.InvokeAsync(kernel.Plugins["GraphPlugin"]["read_email"], new() { ["emailId"] = "email-1" });

        result.GetValue<EmailContentModel>()!.Body.Should().Be("Step 1");
        graph.Verify(g => g.GetEmailAsync("email-1"), Times.Once);
    }
}
