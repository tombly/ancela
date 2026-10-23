using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Ancela.Agent.Services;
using Microsoft.Graph.Models;

namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

/// <summary>
/// The I/O-free parts of turning an email into a PDF: naming the file, rendering the email as one
/// HTML page, and writing its images into that page. <see cref="GraphClient"/> does the Graph calls.
/// </summary>
public static class EmailPdf
{
    /// <summary>Graph converts files to PDF only up to this size (documented limit).</summary>
    public const int ConversionLimitBytes = 2 * 1024 * 1024;

    private const int MaxTitleLength = 100;

    // OneDrive rejects " * : < > ? / \ |. Braces, # and % are legal there but are dropped too: a
    // subject is written by whoever sent the email, and these are the characters that mean
    // something in a URL or a URL template.
    private static readonly SearchValues<char> UnsafeNameChars = SearchValues.Create("\"*:<>?/\\|{}#%");

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex BodyTag = new(@"<body\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ImgTag = new(@"<img\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // The whitespace before src keeps data-src and the like from matching.
    private static readonly Regex SrcAttribute = new(@"(?<=\s)src\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s""'>]+))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SizeAttribute = new(@"(?<=\s)(?<name>width|height)\s*=\s*[""']?(?<v>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The includeLinkedImages parameter, the same on every tool that makes an email a PDF.</summary>
    public const string IncludeLinkedImagesDescription =
        "Whether to download the images the email links to on the sender's servers, which lets the sender see that the email "
        + "was opened. Leave it out unless the user has said: if the email has any, nothing happens yet and the result says what to ask.";

    /// <summary>The result's note when an email links to images and the user hasn't been asked.</summary>
    public static string LinkedImagesQuestion(int count, string notDone) =>
        $"{notDone} The email links to {count} image{(count == 1 ? "" : "s")} on the sender's servers, and downloading "
        + "them lets the sender see that the email was opened. Ask the user whether to include them, then call again "
        + "with includeLinkedImages set to their answer.";

    /// <summary>
    /// "2026-10-13 0915 Subject.pdf". The local received time leads so the folder sorts by date, and
    /// goes to the minute so two same-day emails with one subject (receipts, alerts) don't overwrite
    /// each other. Saving the same email twice yields the same name, and the upload replaces it.
    /// </summary>
    public static string FileName(string? subject, DateTimeOffset received, TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTime(received, timeZone);
        return $"{local.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)} {Title(subject)}.pdf";
    }

    private static string Title(string? subject)
    {
        var cleaned = new StringBuilder();
        foreach (var c in subject ?? string.Empty)
            cleaned.Append(char.IsControl(c) || UnsafeNameChars.Contains(c) ? ' ' : c);

        // SharePoint, which stores OneDrive for Business, refuses any name containing "_vti_".
        var title = Whitespace.Replace(cleaned.ToString(), " ")
            .Replace("_vti_", "_vti ", StringComparison.OrdinalIgnoreCase)
            .Trim();

        if (title.Length > MaxTitleLength)
        {
            var cut = MaxTitleLength;
            if (char.IsHighSurrogate(title[cut - 1]))
                cut--; // don't split an emoji's surrogate pair
            title = title[..cut];
        }

        // A trailing period would read as "Subject..pdf".
        title = title.TrimEnd('.', ' ');
        return title.Length == 0 ? "(no subject)" : title;
    }

    /// <summary>
    /// The email as one HTML page: a From/Sent/To/Cc/Subject block like the one Graph prints for an
    /// .eml, the names of any attachments (their contents are never in the PDF, as with an .eml),
    /// then the original HTML body. Header values come from the sender, so every one is HTML-encoded.
    /// </summary>
    public static string RenderHtml(Message message, IReadOnlyList<string> attachments, TimeZoneInfo timeZone)
    {
        var rows = new List<(string Label, string Value)>
        {
            ("From", Format(message.From)),
            ("Sent", message.SentDateTime is { } sent
                ? $"{TimeZoneInfo.ConvertTime(sent, timeZone).ToString("dddd, d MMMM yyyy HH:mm", CultureInfo.InvariantCulture)} ({timeZone.Id})"
                : string.Empty),
            ("To", Format(message.ToRecipients)),
        };
        if (message.CcRecipients is { Count: > 0 })
            rows.Add(("Cc", Format(message.CcRecipients)));
        rows.Add(("Subject", message.Subject ?? string.Empty));
        if (attachments.Count > 0)
            rows.Add(("Attachments", string.Join(", ", attachments)));

        var header = new StringBuilder("<table style=\"font-family:Segoe UI,Arial,sans-serif;font-size:13px;border-collapse:collapse;margin-bottom:12px\">");
        foreach (var (label, value) in rows)
            header.Append("<tr><td style=\"font-weight:bold;padding:2px 12px 2px 0;vertical-align:top\">")
                .Append(label).Append(":</td><td style=\"padding:2px 0\">")
                .Append(WebUtility.HtmlEncode(value)).Append("</td></tr>");
        header.Append("</table><hr>");

        var body = message.Body?.Content ?? string.Empty;
        if (message.Body?.ContentType == BodyType.Text)
            body = $"<pre style=\"white-space:pre-wrap;font-family:inherit\">{WebUtility.HtmlEncode(body)}</pre>";

        // Keep the email's own <head> (its styles) by inserting just inside <body>; wrap a fragment.
        var bodyTag = BodyTag.Match(body);
        return bodyTag.Success
            ? body.Insert(bodyTag.Index + bodyTag.Length, header.ToString())
            : $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body>{header}{body}</body></html>";
    }

    /// <summary>
    /// Where each picture in <paramref name="html"/> comes from, one entry per &lt;img&gt;, in order:
    /// <c>cid:</c> for one embedded in the email, a web address for one linked from the sender's
    /// servers. Images sized 2 pixels or less are left out: they are there to report that the email
    /// was opened, not to be seen.
    /// </summary>
    public static IReadOnlyList<string> ImageSources(string html) =>
        ImgTag.Matches(html)
            .Where(tag => !IsTrackingPixel(tag.Value))
            .Select(tag => SrcAttribute.Match(tag.Value))
            .Where(src => src.Success)
            .Select(src => Normalize(src.Groups["v"].Value))
            .ToList();

    public static bool IsEmbedded(string source) => source.StartsWith("cid:", StringComparison.OrdinalIgnoreCase);

    public static bool IsLinked(string source) =>
        source.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    /// <summary>The key <see cref="ImageSources"/> gives an embedded image with this Content-ID.</summary>
    public static string EmbeddedSource(string contentId) => $"cid:{contentId.Trim().Trim('<', '>')}";

    /// <summary>
    /// Writes each image in <paramref name="images"/> (keyed as <see cref="ImageSources"/> returns
    /// them) into its &lt;img&gt; as a data: URI, in document order, while the page stays within
    /// <paramref name="limitBytes"/>. Graph's converter shows neither linked images nor an .eml's
    /// embedded (cid:) parts, but does show a data: URI. Returns the page and how many were written.
    /// </summary>
    public static (string Html, int Written) EmbedImages(string html, IReadOnlyDictionary<string, MediaItem> images, int limitBytes)
    {
        var size = Encoding.UTF8.GetPreamble().Length + Encoding.UTF8.GetByteCount(html);
        var written = 0;
        var result = ImgTag.Replace(html, tag =>
        {
            var src = SrcAttribute.Match(tag.Value);
            if (!src.Success || IsTrackingPixel(tag.Value) || !images.TryGetValue(Normalize(src.Groups["v"].Value), out var image))
                return tag.Value;

            var attribute = $"src=\"data:{image.ContentType};base64,{Convert.ToBase64String(image.Bytes)}\"";
            var growth = attribute.Length - Encoding.UTF8.GetByteCount(src.Value);
            if (size + growth > limitBytes)
                return tag.Value;

            size += growth;
            written++;
            return string.Concat(tag.Value.AsSpan(0, src.Index), attribute, tag.Value.AsSpan(src.Index + src.Length));
        });
        return (result, written);
    }

    private static bool IsTrackingPixel(string imgTag)
    {
        int? width = null, height = null;
        foreach (Match size in SizeAttribute.Matches(imgTag))
        {
            var value = int.TryParse(size.Groups["v"].Value, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue;
            if (size.Groups["name"].Value.Equals("width", StringComparison.OrdinalIgnoreCase)) width ??= value;
            else height ??= value;
        }
        return width <= 2 && height <= 2;
    }

    // Attribute values are HTML-encoded (&amp; in a query string), and a cid: may be percent-encoded.
    private static string Normalize(string src)
    {
        var decoded = WebUtility.HtmlDecode(src).Trim();
        return IsEmbedded(decoded) ? EmbeddedSource(Uri.UnescapeDataString(decoded[4..])) : decoded;
    }

    /// <summary>
    /// UTF-8 with a byte-order mark: an email's own HTML may not declare its charset, and the
    /// converter would otherwise have to guess.
    /// </summary>
    public static byte[] Utf8WithBom(string html) => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(html)];

    private static string Format(IEnumerable<Recipient>? recipients) =>
        string.Join("; ", (recipients ?? []).Select(Format).Where(r => r.Length > 0));

    private static string Format(Recipient? recipient)
    {
        var name = recipient?.EmailAddress?.Name;
        var address = recipient?.EmailAddress?.Address;
        return (name, address) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) when name != address => $"{name} <{address}>",
            (_, { Length: > 0 }) => address,
            ({ Length: > 0 }, _) => name,
            _ => string.Empty,
        };
    }
}
