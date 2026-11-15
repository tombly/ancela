using System.IO.Compression;
using System.Xml;
using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using Ancela.Agent.Services;
using FluentAssertions;

namespace Ancela.Agent.Tests;

/// <summary>
/// An email becomes an EPUB the tablet reflows: the header, then the body with its layout, styles
/// and anything active removed, and its pictures as files in the book. Everything in it came from
/// the sender, so the page must be well-formed XHTML whatever the email's HTML was.
/// </summary>
public class EmailEpubTests
{
    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    private static EmailEpub.EmailPage Email(string body, string? subject = "Fall newsletter", bool html = true) => new(
        "\"YGC Wealth\" <news@ygc.example>", new DateTimeOffset(2026, 10, 2, 12, 30, 0, TimeSpan.Zero),
        "tom@example.com", Cc: null, subject, body, BodyIsHtml: html);

    private static (ZipArchive Zip, string Page, EmailEpub.Book Book) Build(
        EmailEpub.EmailPage email, IReadOnlyDictionary<string, MediaItem>? images = null, IReadOnlyList<string>? attachments = null,
        bool linkedImagesWanted = true)
    {
        var book = EmailEpub.Build(email, attachments ?? [], images ?? new Dictionary<string, MediaItem>(), linkedImagesWanted, Pacific);
        var zip = new ZipArchive(new MemoryStream(book.Epub));
        using var reader = new StreamReader(zip.GetEntry("OEBPS/email.xhtml")!.Open());
        return (zip, reader.ReadToEnd(), book);
    }

    private static XmlDocument Xml(string page)
    {
        var document = new XmlDocument { XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(page), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        document.Load(reader);
        return document;
    }

    [Fact]
    public void TheBook_IsAnEpub_WithTheMimetypeFirstAndStored()
    {
        var (zip, _, _) = Build(Email("<p>Hello</p>"));

        var first = zip.Entries[0];
        first.FullName.Should().Be("mimetype");
        first.CompressedLength.Should().Be(first.Length, "OCF requires the mimetype stored, not compressed");
        new StreamReader(first.Open()).ReadToEnd().Should().Be("application/epub+zip");
        zip.Entries.Select(e => e.FullName).Should().Contain(["META-INF/container.xml", "OEBPS/content.opf", "OEBPS/nav.xhtml", "OEBPS/toc.ncx", "OEBPS/style.css"]);
        foreach (var xml in new[] { "META-INF/container.xml", "OEBPS/content.opf", "OEBPS/nav.xhtml", "OEBPS/toc.ncx" })
            Xml(new StreamReader(zip.GetEntry(xml)!.Open()).ReadToEnd());
    }

    [Fact]
    public void TheHeader_GivesTheSubjectAsTitle_AndWhoSentItWhen()
    {
        var (_, page, _) = Build(Email("<p>Hello</p>"), attachments: ["report.pdf"]);

        page.Should().Contain("<h1>Fall newsletter</h1>")
            .And.Contain("From: &quot;YGC Wealth&quot; &lt;news@ygc.example&gt;")
            .And.Contain("Sent: Friday, October 2, 2026 5:30 AM", "sent times are on the user's clock")
            .And.Contain("To: tom@example.com")
            .And.Contain("Attachments: report.pdf");
    }

    [Fact]
    public void ActiveAndHiddenContent_IsRemoved_AndOnlySafeLinksKept()
    {
        var (_, page, _) = Build(Email("""
            <html><head><style>p { color: red }</style><script>alert(1)</script></head>
            <body><div style="display:none;max-height:0">preview text</div>
            <p onclick="steal()" style="color:red" class="x">Read this</p>
            <a href="javascript:steal()">bad</a> <a href="https://ygc.example/q4">good</a>
            <iframe src="https://evil.example"></iframe><form><input value="x"></form></body></html>
            """));

        var body = page[page.IndexOf("<hr/>", StringComparison.Ordinal)..];
        body.Should().Contain("Read this").And.Contain("href=\"https://ygc.example/q4\"");
        body.Should().NotContain("alert").And.NotContain("preview text").And.NotContain("onclick")
            .And.NotContain("style=").And.NotContain("class=").And.NotContain("javascript:")
            .And.NotContain("iframe").And.NotContain("<input");
    }

    [Fact]
    public void LayoutTables_BecomeOneColumn()
    {
        var (_, page, _) = Build(Email("""
            <table><tr><td><table><tr><td>Dear Tom,</td><td>&nbsp;</td></tr></table></td></tr>
            <tr><td><center><font face="Arial">Welcome to October.</font></center></td></tr></table>
            """));

        page.Should().NotContain("<table").And.NotContain("<td").And.NotContain("<font").And.NotContain("<center");
        page.Should().Contain("Dear Tom,").And.Contain("Welcome to October.");
        Xml(page);
    }

    [Fact]
    public void OutlooksPrefixedTags_StillMakeWellFormedXhtml()
    {
        var (_, page, _) = Build(Email("""<p class="MsoNormal">Thanks<o:p></o:p></p><v:shape id="x"><v:textbox>Boxed</v:textbox></v:shape>"""));

        page.Should().Contain("Thanks").And.Contain("Boxed").And.NotContain("o:p").And.NotContain("v:shape");
        Xml(page);
    }

    [Fact]
    public void Pictures_AreFilesInTheBook_OrTheirAltText()
    {
        var images = new Dictionary<string, MediaItem>
        {
            ["cid:logo@ygc"] = new(Png, "image/png"),
            ["https://ygc.example/banner.png?a=1&b=2"] = new(Png, "image/png"),
            ["https://ygc.example/chart.svg"] = new("<svg/>"u8.ToArray(), "image/svg+xml"),
        };
        var (zip, page, book) = Build(Email("""
            <img src="cid:logo@ygc" width="200">
            <img src="https://ygc.example/banner.png?a=1&amp;b=2" alt="Team updates">
            <img src="https://ygc.example/banner.png?a=1&amp;b=2">
            <img src="https://ygc.example/not-downloaded.png" alt="Market chart">
            <img src="https://ygc.example/chart.svg" alt="Chart">
            <img src="https://track.example/open.gif" width="1" height="1">
            <p>Text</p>
            """), images);

        page.Should().Contain("src=\"images/image1.png\"").And.Contain("src=\"images/image2.png\"");
        page.Should().Contain("[Market chart]", "a linked image the owner didn't say yes to shows as its alt text");
        page.Should().Contain("[Chart]", "an SVG can carry scripts, so it's never put in the book");
        page.Should().NotContain("track.example").And.NotContain("width=");
        zip.Entries.Count(e => e.FullName.StartsWith("OEBPS/images/")).Should().Be(2, "the repeated banner is one file");
        book.ImagesMissing.Should().Be(2, "the linked image that wasn't downloaded and the SVG were wanted and aren't there");
        new StreamReader(zip.GetEntry("OEBPS/content.opf")!.Open()).ReadToEnd()
            .Should().Contain("href=\"images/image1.png\" media-type=\"image/png\"");
    }

    [Fact]
    public void AHiddenPicture_IsNotMissing_NorALinkedOneTheOwnerDidntWant()
    {
        var (_, page, book) = Build(Email("""
            <div style="display:none"><img src="cid:mobile@ygc" alt="Mobile banner"></div>
            <img src="https://ygc.example/banner.png" alt="Banner">
            <p>Text</p>
            """), linkedImagesWanted: false);

        book.ImagesMissing.Should().Be(0, "the hidden copy was never to be seen, and the owner said no to linked images");
        page.Should().Contain("[Banner]").And.NotContain("Mobile banner");
    }

    [Fact]
    public void APlainTextEmail_BecomesParagraphs_Escaped()
    {
        var (_, page, _) = Build(Email("Hi Tom,\r\n\r\nLine one\nLine <two> & three\r\n\r\nBye", html: false));

        page.Should().Contain("<p>Hi Tom,</p>").And.Contain("Line one<br />Line &lt;two&gt; &amp; three").And.Contain("<p>Bye</p>");
        Xml(page);
    }

    [Fact]
    public void HeaderValuesFromTheSender_AreEscaped()
    {
        var (zip, page, _) = Build(Email("<p>x</p>", subject: "<script>alert(1)</script> & more"));

        page.Should().Contain("&lt;script&gt;alert(1)&lt;/script&gt; &amp; more").And.NotContain("<script>");
        Xml(new StreamReader(zip.GetEntry("OEBPS/content.opf")!.Open()).ReadToEnd());
    }

    [Fact]
    public void CharactersXmlDoesntAllow_AreDropped()
    {
        var (_, page, _) = Build(Email("<p>Bell\u0007 and form feed\u000C here 🎉</p>", subject: "Odd\u0001 subject"));

        page.Should().Contain("Bell and form feed here 🎉").And.Contain("Odd subject");
        Xml(page);
    }

    [Fact]
    public void WithoutASubject_TheTitleSaysSo()
    {
        var (_, page, _) = Build(Email("<p>x</p>", subject: null));

        page.Should().Contain("<h1>(no subject)</h1>");
    }

    [Fact]
    public void TheStylesheet_IsTheOneTheOwnerChose()
    {
        var (zip, _, _) = Build(Email("<p>x</p>"));

        var css = new StreamReader(zip.GetEntry("OEBPS/style.css")!.Open()).ReadToEnd();
        css.Should().Contain("font-size: 78%; line-height: 1;").And.Contain("h1 { font-size: 1.3em; line-height: 1;").And.Contain("text-align: left");
    }
}
