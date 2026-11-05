using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Ancela.Agent.Services;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using MimeKit.Text;

namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

/// <summary>
/// The owner's iCloud mail over IMAP (imap.mail.me.com) and SMTP (smtp.mail.me.com), signed in with
/// an app-specific password (ICLOUD_USERNAME, the full iCloud Mail address, and ICLOUD_APP_PASSWORD).
/// The same tools and results as Graph's (<see cref="GraphClient"/>), read on 2026-11-05 against the
/// real account: iCloud has no MOVE or SPECIAL-USE extension, but marks Sent Messages and Deleted
/// Messages with their LIST attributes and has UIDPLUS. Ids are the folder and the IMAP UID
/// (<see cref="ImapSearch.EmailId"/>).
/// </summary>
public sealed class ImapMailClient(ILinkedImageFetcher _imageFetcher, IPdfConverter _pdfConverter) : IMailClient, IAsyncDisposable
{
    private const string ImapHost = "imap.mail.me.com";
    private const string SmtpHost = "smtp.mail.me.com";

    /// <summary>The most emails returned per listing, as for Graph.</summary>
    private const int EmailResultCap = 50;

    /// <summary>The length of a listing's preview, as Graph's bodyPreview.</summary>
    private const int PreviewLength = 255;

    /// <summary>The most an attachment may weigh to be read, as for Graph.</summary>
    private const long AttachmentSizeCap = 3_000_000;

    private readonly string _user = Environment.GetEnvironmentVariable("ICLOUD_USERNAME") ?? throw new InvalidOperationException("ICLOUD_USERNAME not set");
    private readonly string _password = Environment.GetEnvironmentVariable("ICLOUD_APP_PASSWORD") ?? throw new InvalidOperationException("ICLOUD_APP_PASSWORD not set");

    // One connection, used one call at a time: signing in takes most of a second, and a DMARC run
    // makes a dozen calls. Messages are handled one at a time anyway (host.json).
    private readonly ImapClient _imap = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IList<IMailFolder>? _folders;

    // Each folder's UIDVALIDITY when last listed. If iCloud renumbers a folder, an id from before
    // could name another email, so a call with one is refused rather than acting on the wrong email.
    private readonly Dictionary<MailFolder, uint> _uidValidity = [];

    public Task<EmailModel[]> GetUserEmailsAsync(MailFolder folder = MailFolder.Inbox, string? search = null) =>
        InFolderAsync(folder, FolderAccess.ReadOnly, listing: true, async mailbox =>
        {
            // A search covers the whole folder, as Graph's does: it is how the model reaches mail
            // older than the newest EmailResultCap.
            var query = string.IsNullOrWhiteSpace(search) ? SearchQuery.All : ImapSearch.Query(search);
            var newest = (await mailbox.SearchAsync(query)).OrderByDescending(u => u.Id).Take(EmailResultCap).ToList();
            if (newest.Count == 0)
                return [];

            var summaries = await mailbox.FetchAsync(newest,
                MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate
                | MessageSummaryItems.Flags | MessageSummaryItems.PreviewText);
            return summaries
                .OrderByDescending(s => s.InternalDate)
                .Select(s => new EmailModel
                {
                    Id = ImapSearch.EmailId(folder, s.UniqueId),
                    Subject = s.Envelope?.Subject ?? string.Empty,
                    From = s.Envelope?.From.Mailboxes.FirstOrDefault()?.Address ?? string.Empty,
                    ReceivedDateTime = s.InternalDate ?? DateTimeOffset.MinValue,
                    BodyPreview = Preview(s.PreviewText),
                    IsRead = s.Flags?.HasFlag(MessageFlags.Seen) ?? false,
                })
                .ToArray();
        });

    public Task<EmailContentModel> GetEmailAsync(string messageId)
    {
        var (folder, uid) = ImapSearch.ParseEmailId(messageId);
        return InFolderAsync(folder, FolderAccess.ReadOnly, listing: false, async mailbox =>
        {
            var summary = await SummaryAsync(mailbox, uid, MessageSummaryItems.InternalDate);
            var message = await mailbox.GetMessageAsync(uid);  // BODY.PEEK: reading doesn't mark it read
            var (body, truncated) = GraphClient.TidyBody(message.TextBody ?? HtmlText(message.HtmlBody));
            return new EmailContentModel
            {
                Id = messageId,
                Subject = message.Subject ?? string.Empty,
                From = message.From.Mailboxes.FirstOrDefault()?.Address ?? string.Empty,
                To = [.. message.To.Mailboxes.Select(m => m.Address)],
                Cc = [.. message.Cc.Mailboxes.Select(m => m.Address)],
                ReceivedDateTime = summary.InternalDate ?? message.Date,
                Body = body,
                Truncated = truncated,
                Attachments = [.. Attachments(message).Select(a => a.Name)],
            };
        });
    }

    public Task<AttachmentContentModel> GetAttachmentAsync(string messageId, string attachmentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attachmentName);
        var (folder, uid) = ImapSearch.ParseEmailId(messageId);
        return InFolderAsync(folder, FolderAccess.ReadOnly, listing: false, async mailbox =>
        {
            await SummaryAsync(mailbox, uid, MessageSummaryItems.UniqueId);
            var message = await mailbox.GetMessageAsync(uid);
            var attachments = Attachments(message);
            var (name, entity) = attachments.FirstOrDefault(a => string.Equals(a.Name, attachmentName, StringComparison.OrdinalIgnoreCase));
            if (entity is null)
                throw new NotInEmailException(attachments.Count == 0
                    ? "That email has no attachments."
                    : $"That email has no attachment named '{attachmentName}'. Its attachments: {string.Join(", ", attachments.Select(a => a.Name))}.");
            if (entity is not MimePart part)
                throw new InvalidOperationException($"{name} is an attached email, not a file, so read_attachment can't open it.");

            using var content = new MemoryStream();
            await part.Content.DecodeToAsync(content);
            if (content.Length > AttachmentSizeCap)
                throw new InvalidOperationException($"{name} is {content.Length / 1_000_000.0:0.#} MB, more than the {AttachmentSizeCap / 1_000_000} MB Ancela downloads.");
            return AttachmentText.Read(name, content.ToArray());
        });
    }

    public Task<string> MarkReadAsync(string messageId, bool read)
    {
        var (folder, uid) = ImapSearch.ParseEmailId(messageId);
        return InFolderAsync(folder, FolderAccess.ReadWrite, listing: false, async mailbox =>
        {
            var summary = await SummaryAsync(mailbox, uid, MessageSummaryItems.Envelope | MessageSummaryItems.Flags);
            var state = read ? "read" : "unread";
            if ((summary.Flags?.HasFlag(MessageFlags.Seen) ?? false) == read)
                return Changed(summary, $"That email is already marked as {state}");

            await mailbox.StoreAsync(uid, new StoreFlagsRequest(read ? StoreAction.Add : StoreAction.Remove, MessageFlags.Seen) { Silent = true });
            return Changed(summary, $"Marked as {state}");
        });
    }

    public Task<string> MoveToDeletedItemsAsync(string messageId)
    {
        var (folder, uid) = ImapSearch.ParseEmailId(messageId);
        return InFolderAsync(folder, FolderAccess.ReadWrite, listing: false, async mailbox =>
        {
            var summary = await SummaryAsync(mailbox, uid, MessageSummaryItems.Envelope);
            if (folder == MailFolder.Deleted)
                return Changed(summary, "That email is already in Deleted Messages");

            // Without MOVE, MailKit copies the email, marks it \Deleted and expunges it. Only UIDPLUS
            // limits that expunge to this one email; without it, it would also remove any other email
            // in the folder marked \Deleted, which here means permanently.
            if (!_imap.Capabilities.HasFlag(ImapCapabilities.Move) && !_imap.Capabilities.HasFlag(ImapCapabilities.UidPlus))
                throw new InvalidOperationException("iCloud's mail server offers neither MOVE nor UIDPLUS, so an email can't be moved safely.");

            await mailbox.MoveToAsync(uid, await FolderAsync(MailFolder.Deleted));
            return Changed(summary, "Moved to Deleted Messages");
        });
    }

    public async Task<string> SendEmailAsync(string toAddress, string subject, string body)
    {
        // A name the model passed for an address ("Bob") fails here rather than at iCloud.
        if (!MailboxAddress.TryParse(toAddress, out var to) || !to.Address.Contains('@'))
            throw new InvalidOperationException($"'{toAddress}' isn't an email address.");

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_user));
        message.To.Add(to);
        message.Subject = subject;
        message.Body = new TextPart(TextFormat.Plain) { Text = body };

        using (var smtp = new SmtpClient())
        {
            await smtp.ConnectAsync(SmtpHost, 587, SecureSocketOptions.StartTls);
            await smtp.AuthenticateAsync(_user, _password);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);
        }

        // Graph kept a copy in Sent Items (SaveToSentItems); iCloud's SMTP server keeps none (tried
        // 2026-11-05: exactly this one copy), so it goes into Sent Messages here, read, where
        // get_recent_emails' Sent folder finds it.
        await InFolderAsync(MailFolder.Sent, FolderAccess.ReadWrite, listing: true,
            async mailbox => await mailbox.AppendAsync(new AppendRequest(message, MessageFlags.Seen)));
        return $"Email sent successfully to {to.Address}";
    }

    public Task<int> GetLinkedImageCountAsync(string messageId)
    {
        var (folder, uid) = ImapSearch.ParseEmailId(messageId);
        return InFolderAsync(folder, FolderAccess.ReadOnly, listing: false, async mailbox =>
        {
            await SummaryAsync(mailbox, uid, MessageSummaryItems.UniqueId);
            var message = await mailbox.GetMessageAsync(uid);
            return EmailPdf.ImageSources(message.HtmlBody ?? string.Empty).Count(EmailPdf.IsLinked);
        });
    }

    /// <summary>
    /// The email as one HTML page, as Graph's (<see cref="EmailPdf"/>): embedded pictures written in,
    /// linked ones only with <paramref name="includeLinkedImages"/>, the owner's yes, since downloading
    /// them tells the sender the email was opened. The page becomes a PDF in the OneDrive folder
    /// (<see cref="IPdfConverter"/>) until Ancela has its own renderer.
    /// </summary>
    public async Task<EmailPdfModel> RenderEmailAsPdfAsync(string messageId, TimeZoneInfo timeZone, bool includeLinkedImages)
    {
        var (folder, uid) = ImapSearch.ParseEmailId(messageId);
        var (message, received) = await InFolderAsync(folder, FolderAccess.ReadOnly, listing: false, async mailbox =>
        {
            var summary = await SummaryAsync(mailbox, uid, MessageSummaryItems.InternalDate);
            return (await mailbox.GetMessageAsync(uid), summary.InternalDate ?? DateTimeOffset.UtcNow);
        });

        var html = EmailPdf.RenderHtml(new EmailPdf.EmailPage(
                Addresses(message.From), message.Date, Addresses(message.To),
                message.Cc.Count > 0 ? Addresses(message.Cc) : null, message.Subject,
                message.HtmlBody ?? message.TextBody ?? string.Empty, BodyIsHtml: message.HtmlBody is not null),
            [.. Attachments(message).Select(a => a.Name)], timeZone);
        if (EmailPdf.Utf8WithBom(html).Length > EmailPdf.ConversionLimitBytes)
            throw new InvalidOperationException("This email is over the 2 MB PDF conversion limit even without its images and attachments, so it can't be made into a PDF.");

        var sources = EmailPdf.ImageSources(html);
        var images = new Dictionary<string, MediaItem>();
        foreach (var part in message.BodyParts.OfType<MimePart>().Where(p => p.ContentId is not null && p.ContentType.MediaType == "image"))
        {
            using var bytes = new MemoryStream();
            await part.Content.DecodeToAsync(bytes);
            if (bytes.Length <= EmailPdf.ConversionLimitBytes)
                images[EmailPdf.EmbeddedSource(part.ContentId)] = new MediaItem(bytes.ToArray(), part.ContentType.MimeType.ToLowerInvariant());
        }
        if (includeLinkedImages)
        {
            foreach (var (url, image) in await _imageFetcher.FetchAsync(sources.Where(EmailPdf.IsLinked)))
                images[url] = image;
        }

        var (page, written) = EmailPdf.EmbedImages(html, images, EmailPdf.ConversionLimitBytes);
        var wanted = sources.Count(s => EmailPdf.IsEmbedded(s) || (includeLinkedImages && EmailPdf.IsLinked(s)));
        return new EmailPdfModel
        {
            Subject = message.Subject,
            ReceivedDateTime = received,
            Pdf = await _pdfConverter.ConvertHtmlToPdfAsync(EmailPdf.Utf8WithBom(page)),
            LinkedImages = sources.Count(EmailPdf.IsLinked),
            ImagesMissing = wanted - written,
        };
    }

    /// <summary>Signs in and selects the Inbox: what check_services reports as "icloud-mail".</summary>
    public Task<int> CheckAsync() =>
        InFolderAsync(MailFolder.Inbox, FolderAccess.ReadOnly, listing: true, mailbox => Task.FromResult(mailbox.Count));

    /// <summary>
    /// Runs <paramref name="work"/> in one of the three folders the tools read, signing in first if
    /// the connection isn't open. iCloud closes an idle connection, so a call that finds it gone
    /// signs in again and runs once more; a call that changes something either ran or didn't.
    /// </summary>
    private async Task<T> InFolderAsync<T>(MailFolder folder, FolderAccess access, bool listing, Func<IMailFolder, Task<T>> work)
    {
        await _lock.WaitAsync();
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await SignInAsync();
                    var mailbox = await FolderAsync(folder);
                    await mailbox.OpenAsync(access);
                    CheckIdsStillHold(folder, mailbox.UidValidity, listing);
                    return await work(mailbox);
                }
                catch (Exception ex) when (attempt == 1 && ex is ServiceNotConnectedException or IOException or SocketException or ImapProtocolException)
                {
                    try { await _imap.DisconnectAsync(false); } catch (Exception) { }
                    _folders = null;
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SignInAsync()
    {
        if (_imap.IsConnected && _imap.IsAuthenticated)
            return;

        if (!_imap.IsConnected)
            await _imap.ConnectAsync(ImapHost, 993, SecureSocketOptions.SslOnConnect);
        // Apple: IMAP takes the name part of the iCloud Mail address, or failing that the full one.
        var at = _user.IndexOf('@');
        try
        {
            await _imap.AuthenticateAsync(at > 0 ? _user[..at] : _user, _password);
        }
        catch (AuthenticationException) when (at > 0)
        {
            await _imap.AuthenticateAsync(_user, _password);
        }
    }

    /// <summary>A folder by its LIST attribute, which iCloud sets without SPECIAL-USE, else Apple's name for it.</summary>
    private async Task<IMailFolder> FolderAsync(MailFolder folder)
    {
        if (folder == MailFolder.Inbox)
            return _imap.Inbox;

        _folders ??= await _imap.GetFoldersAsync(_imap.PersonalNamespaces[0]);
        var (attribute, name) = folder == MailFolder.Sent
            ? (FolderAttributes.Sent, "Sent Messages")
            : (FolderAttributes.Trash, "Deleted Messages");
        return _folders.FirstOrDefault(f => f.Attributes.HasFlag(attribute))
            ?? _folders.FirstOrDefault(f => f.FullName == name)
            ?? throw new InvalidOperationException($"iCloud has no {name} folder.");
    }

    private void CheckIdsStillHold(MailFolder folder, uint uidValidity, bool listing)
    {
        if (!listing && _uidValidity.TryGetValue(folder, out var seen) && seen != uidValidity)
        {
            _uidValidity[folder] = uidValidity;
            throw new InvalidOperationException(
                $"iCloud renumbered the {folder} folder since it was listed, so that id may name another email now. List the emails again.");
        }
        _uidValidity[folder] = uidValidity;
    }

    /// <summary>The email's summary, or an error that says it isn't there, rather than an empty result.</summary>
    private static async Task<IMessageSummary> SummaryAsync(IMailFolder mailbox, UniqueId uid, MessageSummaryItems items) =>
        (await mailbox.FetchAsync([uid], items | MessageSummaryItems.UniqueId)).FirstOrDefault()
        ?? throw new InvalidOperationException($"There's no email with that id in {mailbox.FullName}: it may have been moved or deleted. List the emails again.");

    private static string Changed(IMessageSummary summary, string what) =>
        GraphClient.Changed(summary.Envelope?.Subject, summary.Envelope?.From.Mailboxes.FirstOrDefault()?.Address, what);

    /// <summary>
    /// The email's attachments, as Graph lists them: every file part but the pictures its body shows
    /// (an image with a Content-ID), and attached emails. A report sent as a file, such as a DMARC
    /// report, isn't always marked as an attachment, so a part with a file name counts too.
    /// </summary>
    public static List<(string Name, MimeEntity Entity)> Attachments(MimeMessage message) =>
        [.. message.BodyParts
            .Where(p => p is MessagePart || (p is MimePart part && (part.IsAttachment || part.FileName is not null)
                && !(part.ContentType.MediaType == "image" && part.ContentId is not null && !part.IsAttachment)))
            .Select(p => (p is MimePart part ? part.FileName ?? "(unnamed)" : "(attached email)", p))];

    private static string Addresses(InternetAddressList list) =>
        string.Join("; ", list.Mailboxes.Select(m => string.IsNullOrEmpty(m.Name) || m.Name == m.Address ? m.Address : $"{m.Name} <{m.Address}>"));

    private static string Preview(string? text)
    {
        var preview = Whitespace.Replace(text ?? string.Empty, " ").Trim();
        return preview.Length <= PreviewLength ? preview : preview[..PreviewLength];
    }

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// An HTML body as plain text, for an email with no text part: Graph converted these itself.
    /// Scripts, styles and the head are dropped, block elements become line breaks, and runs of
    /// whitespace in the source become one space, as a browser shows them.
    /// </summary>
    public static string HtmlText(string? html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;

        var text = new StringBuilder();
        var hidden = 0;
        var tokenizer = new HtmlTokenizer(new StringReader(html)) { DecodeCharacterReferences = true };
        while (tokenizer.ReadNextToken(out var token))
        {
            switch (token)
            {
                case HtmlTagToken tag when tag.Id is HtmlTagId.Script or HtmlTagId.Style or HtmlTagId.Head or HtmlTagId.Title:
                    if (tag.IsEndTag)
                        hidden = Math.Max(0, hidden - 1);
                    else if (!tag.IsEmptyElement)
                        hidden++;
                    break;
                case HtmlTagToken tag when tag.Id is HtmlTagId.Br or HtmlTagId.P or HtmlTagId.Div or HtmlTagId.TR or HtmlTagId.LI
                    or HtmlTagId.H1 or HtmlTagId.H2 or HtmlTagId.H3 or HtmlTagId.H4 or HtmlTagId.H5 or HtmlTagId.H6
                    or HtmlTagId.Table or HtmlTagId.BlockQuote or HtmlTagId.HR:
                    text.Append('\n');
                    break;
                case HtmlTagToken { Id: HtmlTagId.TD or HtmlTagId.TH, IsEndTag: true }:
                    text.Append(' ');
                    break;
                case HtmlDataToken data when hidden == 0:
                    text.Append(Whitespace.Replace(data.Data, " "));
                    break;
            }
        }
        return Regex.Replace(text.ToString(), @" *\n *", "\n");
    }

    public async ValueTask DisposeAsync()
    {
        if (_imap.IsConnected)
            await _imap.DisconnectAsync(true);
        _imap.Dispose();
        _lock.Dispose();
    }
}
