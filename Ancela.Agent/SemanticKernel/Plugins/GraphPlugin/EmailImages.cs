using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

/// <summary>
/// An email's pictures: where each comes from (embedded in the email, or linked from the sender's
/// servers), and the question the owner is asked before linked ones are downloaded, since that tells
/// the sender the email was opened. <see cref="EmailEpub"/> puts them in the book.
/// </summary>
public static class EmailImages
{
    private static readonly Regex ImgTag = new(@"<img\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // The whitespace before src keeps data-src and the like from matching.
    private static readonly Regex SrcAttribute = new(@"(?<=\s)src\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s""'>]+))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SizeAttribute = new(@"(?<=\s)(?<name>width|height)\s*=\s*[""']?(?<v>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The includeLinkedImages parameter, on the tool that sends an email to the reMarkable.</summary>
    public const string IncludeLinkedImagesDescription =
        "Whether to download the images the email links to on the sender's servers, which lets the sender see that the email "
        + "was opened. Leave it out unless the user has said: if the email has any, nothing happens yet and the result says what to ask.";

    /// <summary>The result's note when an email links to images and the user hasn't been asked.</summary>
    public static string LinkedImagesQuestion(int count, string notDone) =>
        $"{notDone} The email links to {count} image{(count == 1 ? "" : "s")} on the sender's servers, and downloading "
        + "them lets the sender see that the email was opened. Ask the user whether to include them, then call again "
        + "with includeLinkedImages set to their answer.";

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
            .Select(src => Source(src.Groups["v"].Value))
            .ToList();

    public static bool IsEmbedded(string source) => source.StartsWith("cid:", StringComparison.OrdinalIgnoreCase);

    public static bool IsLinked(string source) =>
        source.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    /// <summary>The key <see cref="ImageSources"/> gives an embedded image with this Content-ID.</summary>
    public static string EmbeddedSource(string contentId) => $"cid:{contentId.Trim().Trim('<', '>')}";

    /// <summary>
    /// An image's source as <see cref="ImageSources"/> keys it. Attribute values are HTML-encoded
    /// (&amp;amp; in a query string), and a cid: may be percent-encoded.
    /// </summary>
    public static string Source(string src)
    {
        var decoded = WebUtility.HtmlDecode(src).Trim();
        return IsEmbedded(decoded) ? EmbeddedSource(Uri.UnescapeDataString(decoded[4..])) : decoded;
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
}
