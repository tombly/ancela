using System.Text.RegularExpressions;

namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

/// <summary>
/// How an email's text reaches the model: its body tidied and capped, and the result of a change
/// naming the email. <see cref="ImapMailClient"/> uses these; <see cref="AttachmentText"/> shares the cap.
/// </summary>
public static class MailText
{
    /// <summary>
    /// The most body text returned for one email (about 5,000 tokens). A newsletter can run to far
    /// more, and the model is told when it was cut.
    /// </summary>
    public const int BodyCap = 20_000;

    private static readonly Regex TrailingSpaces = new(@"[ \t]+\n", RegexOptions.Compiled);
    private static readonly Regex BlankLines = new(@"\n{3,}", RegexOptions.Compiled);

    /// <summary>
    /// An email body as the model gets it: line breaks as \n, no trailing spaces or runs of blank
    /// lines (text converted from HTML carries both, and they cost tokens), cut to <see cref="BodyCap"/>.
    /// </summary>
    public static (string Body, bool Truncated) TidyBody(string? text)
    {
        var body = (text ?? string.Empty).Replace("\r\n", "\n");
        body = BlankLines.Replace(TrailingSpaces.Replace(body, "\n"), "\n\n").Trim();

        var truncated = body.Length > BodyCap;
        if (truncated)
        {
            var cut = BodyCap;
            if (char.IsHighSurrogate(body[cut - 1]))
                cut--; // don't split an emoji's surrogate pair
            body = body[..cut];
        }
        return (body, truncated);
    }

    /// <summary>
    /// The result of a change to an email, naming it by subject and sender. The audit log records the
    /// result, and the id in its arguments can't say later which email it was: a move changes it.
    /// </summary>
    public static string Changed(string? subject, string? from, string what) =>
        $"{what}: \"{(string.IsNullOrEmpty(subject) ? "(no subject)" : subject)}\" from {(string.IsNullOrEmpty(from) ? "an unknown sender" : from)}.";
}
