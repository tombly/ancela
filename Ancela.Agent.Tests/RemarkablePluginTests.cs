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

    private void SetupEmail(string? subject, bool bodyOnly = false, string[]? omitted = null) =>
        _graph
            .Setup(g => g.RenderEmailAsPdfAsync("email-1", It.IsAny<TimeZoneInfo>()))
            .ReturnsAsync(new EmailPdfModel
            {
                Subject = subject,
                ReceivedDateTime = DateTimeOffset.UtcNow,
                Pdf = Pdf,
                BodyOnly = bodyOnly,
                OmittedAttachments = omitted ?? [],
            });

    [Fact]
    public async Task SendEmail_UploadsGraphsPdf_TitledWithTheSubject()
    {
        SetupEmail("Field trip\r\n  permission form ");

        var sent = await Plugin.SendEmailToRemarkableAsync(new Kernel(), "email-1");

        var upload = _uploads.Should().ContainSingle().Subject;
        upload.Title.Should().Be("Field trip permission form");
        upload.Pdf.Should().Equal(Pdf);
        sent.Title.Should().Be("Field trip permission form");
        sent.BodyOnly.Should().BeFalse();
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
    public async Task SendEmail_RendersInTheUsersTimeZone_AndReportsAttachmentsLeftOut()
    {
        SetupEmail("Scans", bodyOnly: true, omitted: ["scan.tiff"]);
        var kernel = new Kernel();
        kernel.Data["userTimeZone"] = "America/Los_Angeles";

        var sent = await Plugin.SendEmailToRemarkableAsync(kernel, "email-1");

        _graph.Verify(g => g.RenderEmailAsPdfAsync("email-1",
            It.Is<TimeZoneInfo>(tz => tz.HasSameRules(TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles")))));
        sent.BodyOnly.Should().BeTrue();
        sent.OmittedAttachments.Should().Equal("scan.tiff");
    }

    [Fact]
    public async Task SendEmail_WhenTheEmailCantBeRendered_SendsNothing()
    {
        _graph
            .Setup(g => g.RenderEmailAsPdfAsync(It.IsAny<string>(), It.IsAny<TimeZoneInfo>()))
            .ThrowsAsync(new InvalidOperationException("Graph returned no email for that id."));

        var act = () => Plugin.SendEmailToRemarkableAsync(new Kernel(), "email-1");

        await act.Should().ThrowAsync<InvalidOperationException>();
        _uploads.Should().BeEmpty();
    }
}
