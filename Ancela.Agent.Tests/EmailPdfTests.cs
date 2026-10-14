using System.Text;
using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using FluentAssertions;
using Microsoft.Graph.Models;

namespace Ancela.Agent.Tests;

/// <summary>
/// Unit tests for the I/O-free half of save_email_to_onedrive: the file name built from a subject
/// that anyone who emails the owner controls, and the body-only HTML used when a message is over
/// Graph's 2 MB conversion limit. OneDriveLiveTests covers the Graph calls.
/// </summary>
public class EmailPdfTests
{
    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

    // 02:30 UTC on the 14th is 19:30 PDT on the 13th.
    private static readonly DateTimeOffset Received = new(2026, 10, 14, 2, 30, 0, TimeSpan.Zero);

    [Fact]
    public void FileName_LeadsWithTheUsersLocalDateAndTime()
    {
        EmailPdf.FileName("Invoice 42", Received, Pacific)
            .Should().Be("2026-10-13 1930 Invoice 42.pdf", because: "an evening email must not be filed under tomorrow's UTC date");
    }

    [Fact]
    public void FileName_StripsPathAndUrlCharactersFromTheSubject()
    {
        var name = EmailPdf.FileName("../../Re: {x} #1 100% <b>a|b</b>?*\"\\", Received, Pacific);

        name.Should().Be("2026-10-13 1930 .. .. Re x 1 100 b a b b.pdf");
        name.Should().NotContainAny(["/", "\\", ":", "{", "}", "#", "%", "<", ">", "|", "?", "*", "\""]);
    }

    [Fact]
    public void FileName_FlattensControlCharactersAndDefusesVti()
    {
        EmailPdf.FileName("Line1\r\nLine2\t_vti_bin", Received, Pacific)
            .Should().Be("2026-10-13 1930 Line1 Line2 _vti bin.pdf", because: "SharePoint refuses names containing _vti_");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("???")]
    [InlineData("...")]
    public void FileName_WithNothingUsableInTheSubject_SaysNoSubject(string? subject)
    {
        EmailPdf.FileName(subject, Received, Pacific).Should().Be("2026-10-13 1930 (no subject).pdf");
    }

    [Fact]
    public void FileName_TrimsTrailingPeriods()
    {
        EmailPdf.FileName("Done...", Received, Pacific).Should().Be("2026-10-13 1930 Done.pdf");
    }

    [Fact]
    public void FileName_TruncatesALongSubjectWithoutSplittingAnEmoji()
    {
        // 99 letters then an emoji (a surrogate pair) straddling the 100-character cut.
        var name = EmailPdf.FileName(new string('a', 99) + "😀 and more", Received, Pacific);

        name.Should().Be($"2026-10-13 1930 {new string('a', 99)}.pdf");
        name.Any(char.IsSurrogate).Should().BeFalse(because: "half a surrogate pair is not valid text");
    }

    [Fact]
    public void BodyOnlyHtml_EncodesTheSenderControlledHeaders()
    {
        var html = EmailPdf.RenderBodyOnlyHtml(
            Message(subject: "<script>alert(1)</script>", fromName: "Eve <b>", body: "<p>Hi</p>"), [], Pacific);

        html.Should().NotContain("<script>").And.Contain("&lt;script&gt;alert(1)&lt;/script&gt;");
        html.Should().Contain("Eve &lt;b&gt; &lt;eve@example.com&gt;");
    }

    [Fact]
    public void BodyOnlyHtml_InsertsTheHeaderInsideBody_KeepingTheEmailsOwnHead()
    {
        const string body = "<html><head><style>p{color:red}</style></head><body class=\"x\"><p>Hi</p></body></html>";

        var html = EmailPdf.RenderBodyOnlyHtml(Message(body: body), [], Pacific);

        html.Should().StartWith("<html><head><style>p{color:red}</style></head><body class=\"x\"><table");
        html.IndexOf("<table", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("<p>Hi</p>", StringComparison.Ordinal));
    }

    [Fact]
    public void BodyOnlyHtml_WrapsAFragmentInADocumentThatDeclaresUtf8()
    {
        var html = EmailPdf.RenderBodyOnlyHtml(Message(body: "<p>Hi</p>"), [], Pacific);

        html.Should().StartWith("<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body><table")
            .And.EndWith("<p>Hi</p></body></html>");
    }

    [Fact]
    public void BodyOnlyHtml_ShowsSentInTheUsersZone_CcOnlyWhenPresent_AndTheOmittedAttachments()
    {
        var withCc = EmailPdf.RenderBodyOnlyHtml(Message(cc: "carol@example.com"), ["big.zip", "scan.pdf"], Pacific);
        var withoutCc = EmailPdf.RenderBodyOnlyHtml(Message(), [], Pacific);

        withCc.Should().Contain("Tuesday, 13 October 2026 19:30 (America/Los_Angeles)");
        withCc.Should().Contain(">Cc:<").And.Contain("carol@example.com");
        withCc.Should().Contain("too large to convert whole: big.zip, scan.pdf");
        withoutCc.Should().NotContain(">Cc:<").And.NotContain(">Attachments:<");
    }

    [Fact]
    public void BodyOnlyHtml_EncodesAPlainTextBody()
    {
        var html = EmailPdf.RenderBodyOnlyHtml(Message(body: "a < b", bodyType: BodyType.Text), [], Pacific);

        html.Should().Contain("<pre style=\"white-space:pre-wrap;font-family:inherit\">a &lt; b</pre>");
    }

    [Fact]
    public void Utf8WithBom_StartsWithTheByteOrderMark()
    {
        var bytes = EmailPdf.Utf8WithBom("café");

        bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Should().Be("café");
    }

    private static Message Message(
        string subject = "Quarterly report", string fromName = "Alice", string? cc = null,
        string body = "<p>Hi</p>", BodyType bodyType = BodyType.Html) => new()
    {
        Subject = subject,
        From = new Recipient { EmailAddress = new EmailAddress { Name = fromName, Address = "eve@example.com" } },
        ToRecipients = [new Recipient { EmailAddress = new EmailAddress { Name = "Owner", Address = "owner@example.com" } }],
        CcRecipients = cc is null ? [] : [new Recipient { EmailAddress = new EmailAddress { Address = cc } }],
        SentDateTime = Received,
        Body = new ItemBody { ContentType = bodyType, Content = body },
    };
}
