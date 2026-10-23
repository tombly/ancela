using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using Ancela.Agent.SemanticKernel.Plugins.RemarkablePlugin;
using FluentAssertions;
using Microsoft.SemanticKernel;
using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// send_email_to_remarkable sends the PDF Graph renders, not text the model passes: with only
/// get_recent_emails' 255-character previews to go on, two emails sent with send_to_remarkable
/// arrived as their title and one line each.
/// </summary>
public class RemarkablePluginTests
{
    private static readonly byte[] Pdf = "%PDF-1.7 the whole email"u8.ToArray();

    private readonly Mock<IGraphClient> _graph = new();
    private readonly Mock<IRemarkableService> _remarkable = new();
    private readonly List<(string Title, byte[] Pdf)> _uploads = [];

    public RemarkablePluginTests()
    {
        _remarkable
            .Setup(r => r.SendPdfAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Callback<string, Stream, CancellationToken>((title, pdf, _) =>
            {
                using var copy = new MemoryStream();
                pdf.CopyTo(copy);
                _uploads.Add((title, copy.ToArray()));
            })
            .ReturnsAsync("doc-1");
    }

    private RemarkablePlugin Plugin => new(_remarkable.Object, _graph.Object);

    private void SetupEmail(string? subject, int imagesMissing = 0) =>
        _graph
            .Setup(g => g.RenderEmailAsPdfAsync("email-1", It.IsAny<TimeZoneInfo>(), It.IsAny<bool>()))
            .ReturnsAsync(new EmailPdfModel
            {
                Subject = subject,
                ReceivedDateTime = DateTimeOffset.UtcNow,
                Pdf = Pdf,
                LinkedImages = 0,
                ImagesMissing = imagesMissing,
            });

    [Fact]
    public async Task SendEmail_UploadsGraphsPdf_TitledWithTheSubject()
    {
        SetupEmail("Field trip\r\n  permission form ");

        var sent = await Plugin.SendEmailToRemarkableAsync(new Kernel(), "email-1");

        var upload = _uploads.Should().ContainSingle().Subject;
        upload.Title.Should().Be("Field trip permission form");
        upload.Pdf.Should().Equal(Pdf);
        sent.Sent.Should().BeTrue();
        sent.Title.Should().Be("Field trip permission form");
        _graph.Verify(g => g.RenderEmailAsPdfAsync("email-1", It.IsAny<TimeZoneInfo>(), false),
            Times.Once, "an email with no linked images has nothing to ask about");
        _remarkable.Verify(r => r.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task SendEmail_WithoutASubject_IsTitledNoSubject(string? subject)
    {
        SetupEmail(subject);

        var sent = await Plugin.SendEmailToRemarkableAsync(new Kernel(), "email-1");

        sent.Title.Should().Be("(no subject)");
        _uploads.Should().ContainSingle().Which.Title.Should().Be("(no subject)");
    }

    [Fact]
    public async Task SendEmail_RendersInTheUsersTimeZone_AndReportsImagesMissing()
    {
        SetupEmail("Scans", imagesMissing: 2);
        var kernel = new Kernel();
        kernel.Data["userTimeZone"] = "America/Los_Angeles";

        var sent = await Plugin.SendEmailToRemarkableAsync(kernel, "email-1");

        _graph.Verify(g => g.RenderEmailAsPdfAsync("email-1",
            It.Is<TimeZoneInfo>(tz => tz.HasSameRules(TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"))), false));
        sent.ImagesMissing.Should().Be(2);
    }

    [Fact]
    public async Task SendEmail_WithLinkedImages_SendsNothingUntilTheUserAnswers()
    {
        // Downloading linked images tells the sender the email was opened, so the user is asked first.
        _graph.Setup(g => g.GetLinkedImageCountAsync("email-1")).ReturnsAsync(12);

        var sent = await Plugin.SendEmailToRemarkableAsync(new Kernel(), "email-1");

        sent.Sent.Should().BeFalse();
        sent.LinkedImages.Should().Be(12);
        sent.Note.Should().StartWith("Nothing was sent yet. The email links to 12 images");
        _graph.Verify(g => g.RenderEmailAsPdfAsync(It.IsAny<string>(), It.IsAny<TimeZoneInfo>(), It.IsAny<bool>()), Times.Never);
        _uploads.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SendEmail_WithTheUsersAnswer_SendsWithoutAskingAgain(bool include)
    {
        _graph.Setup(g => g.GetLinkedImageCountAsync("email-1")).ReturnsAsync(12);
        SetupEmail("Fall catalog");

        var sent = await Plugin.SendEmailToRemarkableAsync(new Kernel(), "email-1", includeLinkedImages: include);

        sent.Sent.Should().BeTrue();
        _graph.Verify(g => g.RenderEmailAsPdfAsync("email-1", It.IsAny<TimeZoneInfo>(), include), Times.Once);
        _graph.Verify(g => g.GetLinkedImageCountAsync(It.IsAny<string>()), Times.Never);
        _uploads.Should().ContainSingle();
    }

    [Fact]
    public async Task SendEmail_WhenTheEmailCantBeRendered_SendsNothing()
    {
        _graph
            .Setup(g => g.RenderEmailAsPdfAsync(It.IsAny<string>(), It.IsAny<TimeZoneInfo>(), It.IsAny<bool>()))
            .ThrowsAsync(new InvalidOperationException("Graph returned no email for that id."));

        var act = () => Plugin.SendEmailToRemarkableAsync(new Kernel(), "email-1");

        await act.Should().ThrowAsync<InvalidOperationException>();
        _uploads.Should().BeEmpty();
    }
}
