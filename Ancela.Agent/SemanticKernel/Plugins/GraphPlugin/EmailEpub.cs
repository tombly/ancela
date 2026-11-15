using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml;
using Ancela.Agent.Services;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Xhtml;

namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

/// <summary>
/// Makes an email an EPUB for the reMarkable, without I/O: the mail client reads the email and its
/// images, this writes the book. The tablet reflows an EPUB, so it needs no renderer here: Microsoft
/// Graph's PDF converter, in a OneDrive folder, did that until 2026-11. The emails sent to
/// the tablet are ones to read later, never receipts or statements (owner, 2026-11-15), so the
/// email's own layout is dropped: its styles, scripts and hidden preview text go, its layout tables
/// become one column, and the stylesheet is the one the owner chose on the tablet (2026-11-15).
/// Images are files in the book: embedded ones always, linked ones only when the owner has said
/// yes, since downloading them tells the sender the email was opened.
/// </summary>
public static class EmailEpub
{
    /// <summary>The largest single image put in a book.</summary>
    public const int ImageLimitBytes = 10 * 1024 * 1024;

    /// <summary>All of a book's images together.</summary>
    public const int ImagesLimitBytes = 50 * 1024 * 1024;

    /// <summary>The text size and spacing the owner settled on, on the tablet, 2026-11-15.</summary>
    public const string Stylesheet = """
        body { font-family: serif; font-size: 78%; line-height: 1; }
        p { margin: 0.3em 0; }
        h1 { font-size: 1.3em; line-height: 1; margin: 0 0 0.2em 0; text-align: left; }
        .meta { font-size: 0.85em; line-height: 1.1; margin: 0 0 0.3em 0; text-align: left; }
        hr { margin: 0.3em 0 0.6em 0; }
        img { max-width: 100%; height: auto; }
        a { color: inherit; }
        """;

    // Removed with everything in them.
    private static readonly HashSet<string> Dropped = new(StringComparer.OrdinalIgnoreCase)
    {
        "head", "script", "style", "link", "meta", "title", "base", "noscript", "template", "iframe", "frame",
        "frameset", "object", "embed", "applet", "form", "input", "button", "select", "option", "textarea",
        "video", "audio", "source", "track", "svg", "math", "canvas", "map", "area",
    };

    // Layout: a cell, a row or a section is its own block, so each becomes a div and the email one column.
    private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "table", "tbody", "thead", "tfoot", "tr", "td", "th", "caption", "colgroup", "col", "center",
        "section", "article", "header", "footer", "main", "nav", "aside", "address", "fieldset", "legend", "details", "summary",
    };

    // Kept as they are. Anything else (font, o:p, an element the HTML spec doesn't have) is replaced by
    // what it holds: XHTML is XML, and a prefixed name like Outlook's o:p isn't well-formed in it.
    private static readonly HashSet<string> Kept = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abbr", "b", "blockquote", "br", "cite", "code", "dd", "del", "div", "dl", "dt", "em", "figcaption",
        "figure", "h1", "h2", "h3", "h4", "h5", "h6", "hr", "i", "img", "ins", "kbd", "li", "mark", "ol", "p", "pre",
        "q", "s", "samp", "small", "span", "strong", "sub", "sup", "u", "ul", "var",
    };

    private static readonly Dictionary<string, string> ImageFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
    };

    /// <summary>
    /// What the book shows of an email. Addresses are as a mail app shows them ("Name &lt;address&gt;",
    /// several joined with "; ").
    /// </summary>
    public sealed record EmailPage(string From, DateTimeOffset? Sent, string To, string? Cc, string? Subject, string Body, bool BodyIsHtml);

    /// <summary>
    /// The book, and how many pictures should be in it and aren't: embedded ones, or linked ones when
    /// they were wanted, that weren't in the images given, were of a kind an EPUB can't hold, or were
    /// too large. Counted after the hidden parts of the email are gone, since a hidden copy (a
    /// newsletter's mobile-only banner) was never to be seen.
    /// </summary>
    public sealed record Book(byte[] Epub, int ImagesMissing);

    /// <summary>
    /// Writes the email as an EPUB: a header (subject as the title; From, Sent, To, Cc and the
    /// names of its attachments, whose contents are never in it), then the body. Each picture whose
    /// source is in <paramref name="images"/>, keyed as <see cref="EmailImages.ImageSources"/> gives
    /// them, becomes a file in the book; any other one is replaced by its alt text.
    /// <paramref name="linkedImagesWanted"/> says whether the owner said yes to linked ones, so that a
    /// linked picture left out counts as missing only then. Header values and the body come from the
    /// sender, so all of it is written as escaped text or parsed HTML.
    /// </summary>
    public static Book Build(EmailPage email, IReadOnlyList<string> attachments,
        IReadOnlyDictionary<string, MediaItem> images, bool linkedImagesWanted, TimeZoneInfo timeZone)
    {
        var parser = new HtmlParser();
        var document = parser.ParseDocument(email.BodyIsHtml ? email.Body : PlainTextAsHtml(email.Body));
        var body = document.Body ?? throw new InvalidOperationException("The email's body could not be read as HTML.");

        // Hidden text (a newsletter's preview line) is found by its style, before styles go.
        foreach (var hidden in body.QuerySelectorAll("[style]").Where(e => IsHidden(e.GetAttribute("style")!)).ToList())
            hidden.Remove();
        foreach (var comment in body.Descendants<IComment>().ToList())
            comment.Remove();

        var (files, missing) = PlaceImages(document, body, images, linkedImagesWanted);
        Simplify(document, body);

        var title = string.IsNullOrWhiteSpace(email.Subject) ? "(no subject)" : email.Subject.Trim();
        var content = string.Concat(body.ChildNodes.Select(n => n.ToHtml(XhtmlMarkupFormatter.Instance)));
        var page = XmlText($"""
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE html>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" lang="en" xml:lang="en">
            <head><meta charset="utf-8"/><title>{X(title)}</title><link rel="stylesheet" type="text/css" href="style.css"/></head>
            <body>
            <h1>{X(title)}</h1>
            <p class="meta">{Header(email, attachments, timeZone)}</p>
            <hr/>
            {content}
            </body>
            </html>
            """);
        EnsureWellFormed(page);

        return new Book(Package(title, Creator(email.From), page, files), missing);
    }

    /// <summary>Places each picture: the image files the book holds, and how many wanted ones got none.</summary>
    private static (List<(string File, MediaItem Image)> Files, int Missing) PlaceImages(
        IDocument document, IElement body, IReadOnlyDictionary<string, MediaItem> images, bool linkedImagesWanted)
    {
        var files = new List<(string File, MediaItem Image)>();
        var bySource = new Dictionary<string, string>();
        long total = 0;
        var missing = 0;
        foreach (var img in body.QuerySelectorAll("img").ToList())
        {
            if (IsTrackingPixel(img))
            {
                img.Remove();
                continue;
            }

            var source = EmailImages.Source(img.GetAttribute("src") ?? string.Empty);
            if (!bySource.TryGetValue(source, out var file)
                && images.TryGetValue(source, out var image)
                && ImageFiles.TryGetValue(image.ContentType, out var extension)
                && image.Bytes.Length <= ImageLimitBytes
                && total + image.Bytes.Length <= ImagesLimitBytes)
            {
                total += image.Bytes.Length;
                file = $"images/image{files.Count + 1}{extension}";
                files.Add((file, image));
                bySource[source] = file;
            }

            if (file is null)
            {
                if (EmailImages.IsEmbedded(source) || (linkedImagesWanted && EmailImages.IsLinked(source)))
                    missing++;
                var alt = img.GetAttribute("alt");
                if (string.IsNullOrWhiteSpace(alt))
                    img.Remove();
                else
                    img.Replace(document.CreateTextNode($"[{alt.Trim()}]"));
                continue;
            }

            foreach (var name in img.Attributes.Select(a => a.Name).Where(n => n is not ("alt")).ToList())
                img.RemoveAttribute(name);
            img.SetAttribute("src", file);
            if (!img.HasAttribute("alt"))
                img.SetAttribute("alt", "");
        }
        return (files, missing);
    }

    /// <summary>Leaves only plain structure: the kept elements, with a link's address and an image's source and alt text.</summary>
    private static void Simplify(IDocument document, IElement body)
    {
        // Deepest first, so each element is handled before the one holding it is replaced.
        foreach (var element in body.Descendants<IElement>().Reverse().ToList())
        {
            var name = element.LocalName;
            if (Dropped.Contains(name))
            {
                element.Remove();
                continue;
            }
            if (Blocks.Contains(name) || (!Kept.Contains(name) && element.Prefix is null && IsBlockish(name)))
            {
                Replace(element, document.CreateElement("div"));
                continue;
            }
            if (!Kept.Contains(name) || element.Prefix is not null)
            {
                Unwrap(element);
                continue;
            }

            foreach (var attribute in element.Attributes.ToList())
            {
                var keep = (name, attribute.Name) switch
                {
                    ("img", "src" or "alt") => true,
                    ("a", "href") => IsSafeLink(attribute.Value),
                    _ => false,
                };
                if (!keep)
                    element.RemoveAttribute(attribute.NamespaceUri, attribute.LocalName);
            }
        }

        // Spacer cells and empty paragraphs left by the layout.
        bool removed;
        do
        {
            removed = false;
            foreach (var empty in body.QuerySelectorAll("div, p, span, a, b, strong, i, em, u, font").Where(e =>
                         string.IsNullOrWhiteSpace(e.TextContent) && e.QuerySelector("img") is null).ToList())
            {
                empty.Remove();
                removed = true;
            }
        } while (removed);
    }

    private static bool IsBlockish(string name) => name is "dir" or "menu" or "listing" or "xmp" or "plaintext";

    private static void Replace(IElement element, IElement replacement)
    {
        while (element.FirstChild is not null)
            replacement.AppendChild(element.FirstChild);
        element.Replace(replacement);
    }

    private static void Unwrap(IElement element)
    {
        var parent = element.Parent ?? throw new InvalidOperationException("An element of the email has no parent.");
        while (element.FirstChild is not null)
            parent.InsertBefore(element.FirstChild, element);
        element.Remove();
    }

    private static bool IsSafeLink(string href) =>
        Uri.TryCreate(href.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto";

    private static bool IsHidden(string style)
    {
        var s = style.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        return s.Contains("display:none", StringComparison.Ordinal) || s.Contains("mso-hide:all", StringComparison.Ordinal)
            || s.Contains("visibility:hidden", StringComparison.Ordinal) || s.Contains("max-height:0;", StringComparison.Ordinal)
            || s.EndsWith("max-height:0", StringComparison.Ordinal);
    }

    // Sized 2 pixels or less: there to report that the email was opened, not to be seen (as EmailImages).
    private static bool IsTrackingPixel(IElement img) =>
        int.TryParse(img.GetAttribute("width"), CultureInfo.InvariantCulture, out var width) && width <= 2
        && int.TryParse(img.GetAttribute("height"), CultureInfo.InvariantCulture, out var height) && height <= 2;

    private static string PlainTextAsHtml(string text)
    {
        var paragraphs = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => $"<p>{string.Join("<br>", p.Split('\n').Select(WebUtility.HtmlEncode))}</p>");
        return $"<html><body>{string.Concat(paragraphs)}</body></html>";
    }

    private static string Header(EmailPage email, IReadOnlyList<string> attachments, TimeZoneInfo timeZone)
    {
        var lines = new List<string> { $"From: {X(email.From)}" };
        if (email.Sent is { } sent)
            lines.Add($"Sent: {X(TimeZoneInfo.ConvertTime(sent, timeZone).ToString("dddd, MMMM d, yyyy h:mm tt", CultureInfo.InvariantCulture))}");
        lines.Add($"To: {X(email.To)}");
        if (!string.IsNullOrEmpty(email.Cc))
            lines.Add($"Cc: {X(email.Cc)}");
        if (attachments.Count > 0)
            lines.Add($"Attachments: {X(string.Join(", ", attachments))}");
        return string.Join("<br/>", lines);
    }

    // "Name <address>" shows as the name; a bare address as itself.
    private static string Creator(string from)
    {
        var first = from.Split("; ")[0];
        var angle = first.IndexOf(" <", StringComparison.Ordinal);
        return (angle > 0 ? first[..angle] : first).Trim('"', ' ');
    }

    /// <summary>The OCF zip: mimetype first and stored, then the package, its two tables of contents
    /// (EPUB 3's nav and EPUB 2's NCX, for older readers), the page, its stylesheet and images.</summary>
    private static byte[] Package(string title, string creator, string page, List<(string File, MediaItem Image)> files)
    {
        var id = $"urn:uuid:{Guid.NewGuid()}";
        var manifest = string.Concat(files.Select((f, i) =>
            $"""    <item id="image{i + 1}" href="{f.File}" media-type="{f.Image.ContentType}"/>""" + "\n"));
        var opf = XmlText($"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="uid" xml:lang="en">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="uid">{id}</dc:identifier>
                <dc:title>{X(title)}</dc:title>
                <dc:creator>{X(creator)}</dc:creator>
                <dc:language>en</dc:language>
                <meta property="dcterms:modified">{DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}</meta>
              </metadata>
              <manifest>
                <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
                <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>
                <item id="css" href="style.css" media-type="text/css"/>
                <item id="email" href="email.xhtml" media-type="application/xhtml+xml"/>
            {manifest}  </manifest>
              <spine toc="ncx"><itemref idref="email"/></spine>
            </package>
            """);
        var nav = XmlText($"""
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE html>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" lang="en" xml:lang="en">
            <head><meta charset="utf-8"/><title>{X(title)}</title></head>
            <body><nav epub:type="toc"><ol><li><a href="email.xhtml">{X(title)}</a></li></ol></nav></body>
            </html>
            """);
        var ncx = XmlText($"""
            <?xml version="1.0" encoding="utf-8"?>
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">
              <head><meta name="dtb:uid" content="{id}"/></head>
              <docTitle><text>{X(title)}</text></docTitle>
              <navMap><navPoint id="email" playOrder="1"><navLabel><text>{X(title)}</text></navLabel><content src="email.xhtml"/></navPoint></navMap>
            </ncx>
            """);
        const string container = """
            <?xml version="1.0" encoding="utf-8"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
            </container>
            """;

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Text(string path, string text, CompressionLevel level = CompressionLevel.Optimal)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path, level).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }

            Text("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Text("META-INF/container.xml", container);
            Text("OEBPS/content.opf", opf);
            Text("OEBPS/nav.xhtml", nav);
            Text("OEBPS/toc.ncx", ncx);
            Text("OEBPS/style.css", Stylesheet);
            Text("OEBPS/email.xhtml", page);
            foreach (var (file, image) in files)
            {
                using var entry = zip.CreateEntry("OEBPS/" + file, CompressionLevel.NoCompression).Open();
                entry.Write(image.Bytes);
            }
        }
        return output.ToArray();
    }

    /// <summary>Throws unless the page parses as XML, which is what a reader requires of XHTML.</summary>
    private static void EnsureWellFormed(string page)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new StringReader(page), settings);
            while (reader.Read()) { }
        }
        catch (XmlException e)
        {
            throw new InvalidOperationException($"The email couldn't be written as an EPUB page: {e.Message}", e);
        }
    }

    // Characters XML 1.0 doesn't allow (control characters an email can carry) are dropped.
    private static string XmlText(string text)
    {
        if (text.All(c => XmlConvert.IsXmlChar(c) || char.IsSurrogate(c)))
            return text;
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (XmlConvert.IsXmlChar(text[i]))
                builder.Append(text[i]);
            else if (i + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[i + 1], text[i]))
                builder.Append(text[i]).Append(text[++i]);
        }
        return builder.ToString();
    }

    private static string X(string text) => WebUtility.HtmlEncode(text);
}
