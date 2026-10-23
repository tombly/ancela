using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Graph.Models;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

/// <summary>
/// The I/O-free parts of saving an email as a PDF: naming the file and, for a message too large to
/// convert whole, rendering an attachment-free HTML stand-in. <see cref="GraphClient"/> does the
/// Graph calls.
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

    /// <summary>
    /// The time zone <see cref="Agent"/> puts in the Chat kernel's data (<c>userTimeZone</c>), or UTC
    /// when it is missing or unknown.
    /// </summary>
    public static TimeZoneInfo UserTimeZone(Kernel kernel) =>
        kernel.Data.TryGetValue("userTimeZone", out var id) && id is string tz
            && TimeZoneInfo.TryFindSystemTimeZoneById(tz, out var found)
            ? found
            : TimeZoneInfo.Utc;

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
    /// The stand-in for a message over <see cref="ConversionLimitBytes"/>: a From/Sent/To/Cc/Subject
    /// block like the one Graph prints for an .eml, the attachments that were left out, then the
    /// original HTML body. Header values come from the sender, so every one is HTML-encoded. Inline
    /// (cid:) images can't be reached from a standalone HTML file and will be missing.
    /// </summary>
    public static string RenderBodyOnlyHtml(Message message, IReadOnlyList<string> omittedAttachments, TimeZoneInfo timeZone)
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
        if (omittedAttachments.Count > 0)
            rows.Add(("Attachments", $"Not included — the email was too large to convert whole: {string.Join(", ", omittedAttachments)}"));

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
