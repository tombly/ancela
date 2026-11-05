using System.Text.RegularExpressions;
using MailKit;
using MailKit.Search;

namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

/// <summary>
/// iCloud email ids, and get_recent_emails' search text as IMAP SEARCH. An id is the folder and the
/// email's IMAP UID, "inbox-4821": a few characters where Graph's are 152, which the model copied
/// wrong one DMARC run in four (2026-11-04). Only the three folders the tools read can be named, so
/// an id from anywhere else the model read can't reach another folder.
/// </summary>
public static class ImapSearch
{
    public static string EmailId(MailFolder folder, UniqueId uid) => $"{Key(folder)}-{uid.Id}";

    /// <summary>The folder and UID an id names, or an error the model can act on.</summary>
    public static (MailFolder Folder, UniqueId Uid) ParseEmailId(string emailId)
    {
        var dash = emailId.Trim().LastIndexOf('-');
        if (dash > 0 && Folder(emailId.Trim()[..dash]) is { } folder
            && uint.TryParse(emailId.Trim()[(dash + 1)..], out var uid) && uid > 0)
            return (folder, new UniqueId(uid));

        throw new InvalidOperationException(
            $"'{emailId}' isn't an email id from get_recent_emails, which look like inbox-4821. List the emails again and use one of those.");
    }

    private static string Key(MailFolder folder) => folder switch
    {
        MailFolder.Inbox => "inbox",
        MailFolder.Sent => "sent",
        MailFolder.Deleted => "deleted",
        _ => throw new ArgumentOutOfRangeException(nameof(folder)),
    };

    private static MailFolder? Folder(string key) => key.ToLowerInvariant() switch
    {
        "inbox" => MailFolder.Inbox,
        "sent" => MailFolder.Sent,
        "deleted" => MailFolder.Deleted,
        _ => null,
    };

    // A word or a "quoted phrase", optionally after from:, to:, subject: or body:.
    private static readonly Regex Term = new(@"(?:(?<field>from|to|subject|body):)?(?:""(?<text>[^""]*)""|(?<text>\S+))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The model's search text, which get_recent_emails describes as Graph took it ("from:someone@example.com",
    /// "invoice march"), as IMAP SEARCH: each word must appear somewhere in the email (TEXT: headers and
    /// body), "from:" and the like narrow one to a field, OR between two terms takes either, and AND is
    /// the default. Every term is literal text to IMAP, so the search can only find emails, not change any.
    /// </summary>
    public static SearchQuery Query(string search)
    {
        SearchQuery? query = null;
        var or = false;
        foreach (Match match in Term.Matches(search))
        {
            var text = match.Groups["text"].Value;
            var keyword = !match.Groups["field"].Success && !match.Value.StartsWith('"');
            if (keyword && text.Equals("AND", StringComparison.OrdinalIgnoreCase))
                continue;
            if (keyword && text.Equals("OR", StringComparison.OrdinalIgnoreCase))
            {
                or = query is not null;
                continue;
            }
            if (text.Length == 0)
                continue;

            var term = match.Groups["field"].Value.ToLowerInvariant() switch
            {
                "from" => SearchQuery.FromContains(text),
                "to" => SearchQuery.ToContains(text),
                "subject" => SearchQuery.SubjectContains(text),
                "body" => SearchQuery.BodyContains(text),
                _ => SearchQuery.MessageContains(text),
            };
            query = query is null ? term : or ? query.Or(term) : query.And(term);
            or = false;
        }
        return query ?? SearchQuery.All;
    }
}
