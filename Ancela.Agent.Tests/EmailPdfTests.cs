using System.Text;
using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using Ancela.Agent.Services;
using FluentAssertions;
using Microsoft.Graph.Models;

namespace Ancela.Agent.Tests;

/// <summary>
/// Unit tests for the I/O-free half of turning an email into a PDF: the file name built from a
/// subject that anyone who emails the owner controls, the HTML page Graph converts, and writing the
/// email's images into that page. OneDriveLiveTests covers the Graph calls.
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
    public void Html_EncodesTheSenderControlledHeaders()
    {
        var html = EmailPdf.RenderHtml(
            Message(subject: "<script>alert(1)</script>", fromName: "Eve <b>", body: "<p>Hi</p>"), [], Pacific);

        html.Should().NotContain("<script>").And.Contain("&lt;script&gt;alert(1)&lt;/script&gt;");
        html.Should().Contain("Eve &lt;b&gt; &lt;eve@example.com&gt;");
    }

    [Fact]
    public void Html_InsertsTheHeaderInsideBody_KeepingTheEmailsOwnHead()
    {
        const string body = "<html><head><style>p{color:red}</style></head><body class=\"x\"><p>Hi</p></body></html>";

        var html = EmailPdf.RenderHtml(Message(body: body), [], Pacific);

        html.Should().StartWith("<html><head><style>p{color:red}</style></head><body class=\"x\"><table");
        html.IndexOf("<table", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("<p>Hi</p>", StringComparison.Ordinal));
    }

    [Fact]
    public void Html_WrapsAFragmentInADocumentThatDeclaresUtf8()
    {
        var html = EmailPdf.RenderHtml(Message(body: "<p>Hi</p>"), [], Pacific);

        html.Should().StartWith("<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body><table")
            .And.EndWith("<p>Hi</p></body></html>");
    }

    [Fact]
    public void Html_ShowsSentInTheUsersZone_AndCcAndAttachmentsOnlyWhenPresent()
    {
        var withCc = EmailPdf.RenderHtml(Message(cc: "carol@example.com"), ["big.zip", "scan.pdf"], Pacific);
        var withoutCc = EmailPdf.RenderHtml(Message(), [], Pacific);

        withCc.Should().Contain("Tuesday, 13 October 2026 19:30 (America/Los_Angeles)");
        withCc.Should().Contain(">Cc:<").And.Contain("carol@example.com");
        withCc.Should().Contain(">Attachments:<").And.Contain("big.zip, scan.pdf");
        withoutCc.Should().NotContain(">Cc:<").And.NotContain(">Attachments:<");
    }

    [Fact]
    public void Html_EncodesAPlainTextBody()
    {
        var html = EmailPdf.RenderHtml(Message(body: "a < b", bodyType: BodyType.Text), [], Pacific);

        html.Should().Contain("<pre style=\"white-space:pre-wrap;font-family:inherit\">a &lt; b</pre>");
    }

    [Fact]
    public void ImageSources_ListsEmbeddedAndLinkedImages_DecodedAndInOrder()
    {
        var html = """
            <img alt="logo" src="https://cdn.example.com/a.png?x=1&amp;y=2">
            <img data-src="https://lazy.example.com/b.png" src='cid:part1%40example.com'>
            <img src=https://cdn.example.com/c.gif width="600" height="200">
            <img src="data:image/png;base64,AAAA">
            """;

        EmailPdf.ImageSources(html).Should().Equal(
            "https://cdn.example.com/a.png?x=1&y=2", "cid:part1@example.com", "https://cdn.example.com/c.gif", "data:image/png;base64,AAAA");
    }

    [Theory]
    [InlineData("<img src=\"https://t.example.com/open.gif\" width=\"1\" height=\"1\">")]
    [InlineData("<img width=0 height=0 src=\"https://t.example.com/open.gif\">")]
    [InlineData("<img src=\"https://t.example.com/open.gif\" height=\"2\" width=\"2\" style=\"display:none\">")]
    public void ImageSources_LeavesOutTrackingPixels(string html)
    {
        EmailPdf.ImageSources(html).Should().BeEmpty(because: "a pixel-sized image is there to report the open, not to be seen");
    }

    [Fact]
    public void ImageSources_KeepsAnImageWithOnlyOneSmallDimension()
    {
        EmailPdf.ImageSources("<img src=\"https://cdn.example.com/rule.png\" width=\"600\" height=\"1\">").Should().HaveCount(1);
    }

    [Fact]
    public void LinkedAndEmbedded_AreToldApartByScheme()
    {
        EmailPdf.IsLinked("https://cdn.example.com/a.png").Should().BeTrue();
        EmailPdf.IsLinked("http://cdn.example.com/a.png").Should().BeTrue();
        EmailPdf.IsLinked("cid:part1").Should().BeFalse();
        EmailPdf.IsLinked("data:image/png;base64,AAAA").Should().BeFalse();
        EmailPdf.IsEmbedded("CID:part1").Should().BeTrue();
        EmailPdf.EmbeddedSource("<part1@example.com>").Should().Be("cid:part1@example.com");
    }

    [Fact]
    public void EmbedImages_WritesEachImageAsADataUri_AndLeavesTheRest()
    {
        var html = """<p><img src="cid:logo" alt="Logo"><img src="https://cdn.example.com/a.png?x=1&amp;y=2"><img src="https://cdn.example.com/missing.png"></p>""";
        var images = new Dictionary<string, MediaItem>
        {
            ["cid:logo"] = new([1, 2, 3], "image/png"),
            ["https://cdn.example.com/a.png?x=1&y=2"] = new([4, 5, 6], "image/jpeg"),
        };

        var (page, written) = EmailPdf.EmbedImages(html, images, EmailPdf.ConversionLimitBytes);

        written.Should().Be(2);
        page.Should().Be("""<p><img src="data:image/png;base64,AQID" alt="Logo"><img src="data:image/jpeg;base64,BAUG"><img src="https://cdn.example.com/missing.png"></p>""");
    }

    [Fact]
    public void EmbedImages_StopsAtTheLimit_KeepingTheEarlierImages()
    {
        var html = """<img src="cid:a"><img src="cid:b">""";
        var bytes = new byte[300];
        var images = new Dictionary<string, MediaItem> { ["cid:a"] = new(bytes, "image/png"), ["cid:b"] = new(bytes, "image/png") };
        var oneImage = Encoding.UTF8.GetPreamble().Length + EmailPdf.EmbedImages("""<img src="cid:a">""", images, int.MaxValue).Html.Length
            + """<img src="cid:b">""".Length;

        var (page, written) = EmailPdf.EmbedImages(html, images, oneImage);

        written.Should().Be(1, because: "the second image would take the page past the limit");
        page.Should().StartWith("<img src=\"data:image/png;base64,").And.EndWith("<img src=\"cid:b\">");
        (Encoding.UTF8.GetPreamble().Length + Encoding.UTF8.GetByteCount(page)).Should().BeLessThanOrEqualTo(oneImage);
    }

    [Fact]
    public void LinkedImagesQuestion_NamesTheCountAndTheTradeOff()
    {
        var note = EmailPdf.LinkedImagesQuestion(1, "Nothing was sent yet.");

        note.Should().StartWith("Nothing was sent yet. The email links to 1 image on the sender's servers")
            .And.Contain("lets the sender see that the email was opened")
            .And.Contain("includeLinkedImages");
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
