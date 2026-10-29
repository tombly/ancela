using System.Globalization;
using System.Text.RegularExpressions;
using Ancela.Agent.Services;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

public interface IGraphClient
{
    Task<EventModel[]> GetUserEventsAsync(DateTimeOffset start, DateTimeOffset end);
    Task<EventModel> CreateEventAsync(string subject, string start, string end, string? location = null, string? body = null, bool isAllDay = false);
    Task<EmailModel[]> GetUserEmailsAsync(MailFolder folder = MailFolder.Inbox, string? search = null);
    Task<EmailContentModel> GetEmailAsync(string messageId);
    Task<AttachmentContentModel> GetAttachmentAsync(string messageId, string attachmentName);
    Task<string> MarkReadAsync(string messageId, bool read);
    Task<string> MoveToDeletedItemsAsync(string messageId);
    Task<string> SendEmailAsync(string toAddress, string subject, string body);
    Task<int> GetLinkedImageCountAsync(string messageId);
    Task<SavedEmailModel> SaveEmailAsPdfAsync(string messageId, TimeZoneInfo timeZone, bool includeLinkedImages);
    Task<EmailPdfModel> RenderEmailAsPdfAsync(string messageId, TimeZoneInfo timeZone, bool includeLinkedImages);
    Task<string> GetOneDriveFolderNameAsync();
    Task<ContactModel[]> GetUserContactsAsync(int maxResults = 100);
    Task<ContactModel?> GetUserContactByNameAsync(string name);
}

/// <summary>
/// Service for interacting with Microsoft Graph API.
/// </summary>
public class GraphClient : IGraphClient
{
    /// <summary>
    /// The Graph service client for app-only authentication.
    /// </summary>
    private readonly GraphServiceClient _appClient;

    /// <summary>
    /// The Entra ID of the user.
    /// </summary>
    private readonly string _entraUserId;

    private readonly ILinkedImageFetcher _imageFetcher;

    public GraphClient(ILinkedImageFetcher? imageFetcher = null)
    {
        _imageFetcher = imageFetcher ?? new LinkedImageFetcher();

        _entraUserId = Environment.GetEnvironmentVariable("GRAPH_USER_ID") ?? throw new Exception("GRAPH_USER_ID not set");

        var tenantId = Environment.GetEnvironmentVariable("GRAPH_TENANT_ID") ?? throw new Exception("GRAPH_TENANT_ID not set");
        var clientId = Environment.GetEnvironmentVariable("GRAPH_CLIENT_ID") ?? throw new Exception("GRAPH_CLIENT_ID not set");
        var clientSecret = Environment.GetEnvironmentVariable("GRAPH_CLIENT_SECRET") ?? throw new Exception("GRAPH_CLIENT_SECRET not set");

        var clientSecretCredential = new ClientSecretCredential(tenantId, clientId, clientSecret);

        // Use the default scope, which will request the scopes configured on the
        // app registration
        _appClient = new GraphServiceClient(clientSecretCredential, ["https://graph.microsoft.com/.default"]);
    }

    #region Calendar

    public async Task<EventModel[]> GetUserEventsAsync(DateTimeOffset start, DateTimeOffset end)
    {
        var startDate = start.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
        var endDate = end.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

        // Get all calendars in the user's default calendar group. A missing response is a failure,
        // not an empty calendar: skipping it would report "no events" for a calendar never read.
        var calendars = await _appClient.Users[_entraUserId].Calendars.GetAsync();
        if (calendars?.Value == null)
            throw new InvalidOperationException("Graph returned no response for the calendar list.");

        var allEvents = new List<Event>();

        // Pull each calendar's events with CalendarView, which expands recurring series
        // into their individual occurrences within the window. A plain /events query
        // returns only single events and series *masters* (whose start/end are the first
        // occurrence), so recurring meetings on a given day are silently missed.
        foreach (var calendar in calendars.Value)
        {
            var events = await _appClient.Users[_entraUserId].Calendars[calendar.Id].CalendarView.GetAsync((config) =>
            {
                // CalendarView takes the range as query parameters (not a $filter) and
                // returns every occurrence overlapping it, including multi-day events.
                config.QueryParameters.StartDateTime = startDate;
                config.QueryParameters.EndDateTime = endDate;
                // Request specific properties.
                config.QueryParameters.Select = ["subject", "start", "end", "organizer"];
                // Get at most 50 results per calendar.
                config.QueryParameters.Top = 50;
                // Sort by start time.
                config.QueryParameters.Orderby = ["start/dateTime"];
                // Return start/end in UTC so they parse unambiguously below.
                config.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
            });

            allEvents.AddRange(events?.Value
                ?? throw new InvalidOperationException($"Graph returned no response for the calendar '{calendar.Name}'."));
        }

        // Map to model and sort by absolute start instant across all calendars.
        return allEvents
            .Where(e => e.Start?.DateTime != null && e.End?.DateTime != null)
            .Select(e => new EventModel
            {
                Description = e.Subject ?? string.Empty,
                Start = ParseGraphUtc(e.Start!.DateTime!),
                End = ParseGraphUtc(e.End!.DateTime!)
            })
            .OrderBy(e => e.Start)
            .ToArray();
    }

    // CalendarView returns times as a zone-less string (e.g. "2026-06-04T13:00:00.0000000")
    // alongside a separate TimeZone field. With the Prefer: outlook.timezone="UTC" header that
    // zone is UTC, so parse the string as universal rather than letting DateTimeOffset assume
    // the host's local zone (which would be wrong anywhere but a UTC host).
    private static DateTimeOffset ParseGraphUtc(string dateTime) =>
        DateTimeOffset.Parse(dateTime, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    /// <summary>
    /// Creates a new calendar event. The start/end dates must be in the format:
    /// "yyyy-MM-ddTHH:mm:ss" and if isAllDay is true then the time value must be
    /// midnight.
    public async Task<EventModel> CreateEventAsync(string subject, string start, string end, string? location = null, string? body = null, bool isAllDay = false)
    {
        var newEvent = new Event
        {
            Subject = subject,
            Start = new DateTimeTimeZone
            {
                DateTime = start,
                TimeZone = TimeZoneInfo.Local.Id
            },
            End = new DateTimeTimeZone
            {
                DateTime = end,
                TimeZone = TimeZoneInfo.Local.Id
            },
            IsAllDay = isAllDay
        };

        if (!string.IsNullOrEmpty(location))
        {
            newEvent.Location = new Location { DisplayName = location };
        }

        if (!string.IsNullOrEmpty(body))
        {
            newEvent.Body = new ItemBody
            {
                ContentType = BodyType.Text,
                Content = body
            };
        }

        var createdEvent = await _appClient.Users[_entraUserId].Events.PostAsync(newEvent);

        return new EventModel
        {
            Description = createdEvent?.Subject ?? string.Empty,
            Start = DateTimeOffset.Parse(createdEvent?.Start?.DateTime ?? string.Empty),
            End = DateTimeOffset.Parse(createdEvent?.End?.DateTime ?? string.Empty)
        };
    }

    #endregion

    #region Email

    /// <summary>
    /// The most messages returned per call, so the payload stays bounded.
    /// </summary>
    private const int EmailResultCap = 50;

    // No date window: EmailResultCap is always the tighter limit, so a window could never return
    // an email the cap would not, only hide some, and the model passed one unasked (30, then 90
    // days). Every result carries its received date, so the model narrows by date itself.
    public async Task<EmailModel[]> GetUserEmailsAsync(MailFolder folder = MailFolder.Inbox, string? search = null)
    {
        var folderId = WellKnownName(folder);

        // A search covers the whole folder: it is how the model reaches mail older than the
        // newest EmailResultCap, which in a busy inbox is a day or two.
        var searching = !string.IsNullOrWhiteSpace(search);

        var messages = await _appClient.Users[_entraUserId].MailFolders[folderId].Messages.GetAsync((config) =>
        {
            // Request specific properties.
            config.QueryParameters.Select = ["id", "subject", "from", "receivedDateTime", "bodyPreview", "isRead"];
            // Safety cap: at most the most-recent results.
            config.QueryParameters.Top = EmailResultCap;
            if (searching)
            {
                // Graph rejects $orderby alongside $search (400 SearchWithOrderBy) and returns
                // matches newest first anyway.
                config.QueryParameters.Search = SearchClause(search!);
            }
            else
            {
                // Sort by received date, most recent first.
                config.QueryParameters.Orderby = ["receivedDateTime desc"];
            }
        });

        // A missing response is a failure; an empty folder comes back as an empty list.
        if (messages?.Value == null)
            throw new InvalidOperationException("Graph returned no response for the email list.");

        return messages.Value
            .Select(m => new EmailModel
            {
                Id = m.Id ?? string.Empty,
                Subject = m.Subject ?? string.Empty,
                From = m.From?.EmailAddress?.Address ?? string.Empty,
                ReceivedDateTime = m.ReceivedDateTime ?? DateTimeOffset.MinValue,
                BodyPreview = m.BodyPreview ?? string.Empty,
                IsRead = m.IsRead ?? false
            }).ToArray();
    }

    /// <summary>Graph's well-known name for one of the folders the agent may read.</summary>
    private static string WellKnownName(MailFolder folder) => folder switch
    {
        MailFolder.Inbox => "inbox",
        MailFolder.Sent => "sentitems",
        MailFolder.Deleted => "deleteditems",
        _ => "inbox"
    };

    /// <summary>
    /// The most body text returned for one email (about 5,000 tokens). A newsletter can run to far
    /// more, and the model is told when it was cut.
    /// </summary>
    public const int EmailBodyCap = 20_000;

    private static readonly Regex TrailingSpaces = new(@"[ \t]+\n", RegexOptions.Compiled);
    private static readonly Regex BlankLines = new(@"\n{3,}", RegexOptions.Compiled);

    // Ids of the folders in MailFolder, looked up once: they don't change.
    private string[]? _readableFolderIds;

    /// <summary>
    /// One email in full, with its body as text. get_recent_emails returns only Graph's bodyPreview,
    /// the first 255 characters, which is too little to answer what an email says.
    /// </summary>
    public async Task<EmailContentModel> GetEmailAsync(string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        var request = _appClient.Users[_entraUserId].Messages[messageId];
        var message = await request.GetAsync(config =>
        {
            config.QueryParameters.Select = ["id", "subject", "from", "toRecipients", "ccRecipients", "receivedDateTime", "body", "hasAttachments", "parentFolderId"];
            // Graph converts an HTML body to text, which is all the model needs and far shorter.
            config.Headers.Add("Prefer", "outlook.body-content-type=\"text\"");
        }) ?? throw new InvalidOperationException("Graph returned no email for that id.");

        await CheckReadableFolderAsync(message);

        // hasAttachments leaves out pictures in the body, so an email with only those needs no list.
        if (message.HasAttachments == true)
            message.Attachments = [.. await FileAttachmentsAsync(request)];
        return ToEmailContent(message);
    }

    /// <summary>
    /// The most an attachment may weigh to be downloaded. Only the first <see cref="AttachmentText.TextCap"/>
    /// characters are read, so this bounds the download, not what the model sees.
    /// </summary>
    private const long AttachmentSizeCap = 3_000_000;

    /// <summary>
    /// One attachment's text, unpacked from a .zip or .gz (<see cref="AttachmentText"/>). Reports sent
    /// as files, such as DMARC aggregate reports, have nothing in the email's body.
    /// </summary>
    public async Task<AttachmentContentModel> GetAttachmentAsync(string messageId, string attachmentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(attachmentName);

        var request = _appClient.Users[_entraUserId].Messages[messageId];
        var message = await request.GetAsync(config => config.QueryParameters.Select = ["parentFolderId"])
            ?? throw new InvalidOperationException("Graph returned no email for that id.");
        await CheckReadableFolderAsync(message);

        var attachments = await FileAttachmentsAsync(request);
        var match = attachments.FirstOrDefault(a => string.Equals(a.Name, attachmentName, StringComparison.OrdinalIgnoreCase))
            ?? throw new NotInEmailException(attachments.Length == 0
                ? "That email has no attachments."
                : $"That email has no attachment named '{attachmentName}'. Its attachments: {string.Join(", ", attachments.Select(a => a.Name))}.");
        if (match.Size > AttachmentSizeCap)
            throw new InvalidOperationException($"{match.Name} is {match.Size / 1_000_000.0:0.#} MB, more than the {AttachmentSizeCap / 1_000_000} MB Ancela downloads.");

        return await request.Attachments[match.Id].GetAsync() switch
        {
            FileAttachment { ContentBytes: { } bytes } => AttachmentText.Read(match.Name ?? attachmentName, bytes),
            ItemAttachment => throw new InvalidOperationException($"{match.Name} is an attached email or calendar item, not a file, so read_attachment can't open it."),
            ReferenceAttachment => throw new InvalidOperationException($"{match.Name} is a link to a file stored elsewhere, not a file attached to the email."),
            _ => throw new InvalidOperationException("Graph returned no content for the attachment."),
        };
    }

    /// <summary>
    /// Marks an email in the Inbox, Sent or Deleted folder as read or unread. Unlike a move, this
    /// keeps the email's id.
    /// </summary>
    public async Task<string> MarkReadAsync(string messageId, bool read)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        var request = _appClient.Users[_entraUserId].Messages[messageId];
        var message = await request.GetAsync(config => config.QueryParameters.Select = ["parentFolderId", "subject", "from", "isRead"])
            ?? throw new InvalidOperationException("Graph returned no email for that id.");
        await CheckReadableFolderAsync(message);
        var state = read ? "read" : "unread";
        if (message.IsRead == read)
            return Changed(message, $"That email is already marked as {state}");

        _ = await request.PatchAsync(new Message { IsRead = read })
            ?? throw new InvalidOperationException("Graph returned no email for the change.");
        return Changed(message, $"Marked as {state}");
    }

    /// <summary>
    /// Moves an email from the Inbox or Sent folder to Deleted Items, where Ancela can still read it
    /// and the owner can restore it. Never a permanent delete.
    /// </summary>
    public async Task<string> MoveToDeletedItemsAsync(string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        var request = _appClient.Users[_entraUserId].Messages[messageId];
        var message = await request.GetAsync(config => config.QueryParameters.Select = ["parentFolderId", "subject", "from"])
            ?? throw new InvalidOperationException("Graph returned no email for that id.");
        await CheckReadableFolderAsync(message);
        if (message.ParentFolderId == (await ReadableFolderIdsAsync())[(int)MailFolder.Deleted])
            return Changed(message, "That email is already in Deleted Items");

        _ = await request.Move.PostAsync(new Microsoft.Graph.Users.Item.Messages.Item.Move.MovePostRequestBody { DestinationId = "deleteditems" })
            ?? throw new InvalidOperationException("Graph returned no email for the move.");
        return Changed(message, "Moved to Deleted Items");
    }

    /// <summary>
    /// The result of a change to an email, naming it by subject and sender. The audit log records the
    /// result, and the id in its arguments can't say later which email it was: a move changes it.
    /// </summary>
    public static string Changed(Message message, string what) =>
        $"{what}: \"{message.Subject ?? "(no subject)"}\" from {message.From?.EmailAddress?.Address ?? "an unknown sender"}.";

    /// <summary>The email's attachments, pictures in the body aside, without their content.</summary>
    private static async Task<Attachment[]> FileAttachmentsAsync(Microsoft.Graph.Users.Item.Messages.Item.MessageItemRequestBuilder request)
    {
        // No content: without $select, file attachments come back with it.
        var attachments = (await request.Attachments.GetAsync(config =>
            config.QueryParameters.Select = ["id", "name", "isInline", "size"]))?.Value
            ?? throw new InvalidOperationException("Graph returned no attachment list for the email.");
        return [.. attachments.Where(a => a.IsInline != true)];
    }

    /// <summary>
    /// The same folders get_recent_emails reads. Its results are where ids come from, but an id can
    /// also come from anywhere else the model has read, such as another email's text.
    /// </summary>
    private async Task CheckReadableFolderAsync(Message message)
    {
        if (!(await ReadableFolderIdsAsync()).Contains(message.ParentFolderId))
            throw new InvalidOperationException("That email is not in the Inbox, Sent or Deleted folder, the only ones Ancela reads.");
    }

    // In MailFolder's order, so an id can be looked up by its folder.
    private async Task<string[]> ReadableFolderIdsAsync() =>
        _readableFolderIds ??= await Task.WhenAll(Enum.GetValues<MailFolder>().Select(async folder =>
            (await _appClient.Users[_entraUserId].MailFolders[WellKnownName(folder)].GetAsync(config => config.QueryParameters.Select = ["id"]))?.Id
            ?? throw new InvalidOperationException($"Graph returned no id for the {folder} folder.")));

    /// <summary>
    /// Maps a message fetched with a text body, tidied and cut to <see cref="EmailBodyCap"/>. Text
    /// converted from HTML carries runs of blank lines and trailing spaces that cost tokens.
    /// <see cref="Message.Attachments"/> holds the file attachments' names when there are any.
    /// </summary>
    public static EmailContentModel ToEmailContent(Message message)
    {
        var body = (message.Body?.Content ?? string.Empty).Replace("\r\n", "\n");
        body = BlankLines.Replace(TrailingSpaces.Replace(body, "\n"), "\n\n").Trim();

        var truncated = body.Length > EmailBodyCap;
        if (truncated)
        {
            var cut = EmailBodyCap;
            if (char.IsHighSurrogate(body[cut - 1]))
                cut--; // don't split an emoji's surrogate pair
            body = body[..cut];
        }

        return new EmailContentModel
        {
            Id = message.Id ?? string.Empty,
            Subject = message.Subject ?? string.Empty,
            From = message.From?.EmailAddress?.Address ?? string.Empty,
            To = Addresses(message.ToRecipients),
            Cc = Addresses(message.CcRecipients),
            ReceivedDateTime = message.ReceivedDateTime ?? DateTimeOffset.MinValue,
            Body = body,
            Truncated = truncated,
            Attachments = (message.Attachments ?? []).Where(a => a.IsInline != true).Select(a => a.Name ?? "(unnamed)").ToArray(),
        };
    }

    private static string[] Addresses(List<Recipient>? recipients) =>
        (recipients ?? []).Select(r => r.EmailAddress?.Address).OfType<string>().ToArray();

    /// <summary>
    /// Wraps the model's search text for Graph's <c>$search</c>, which takes one double-quoted KQL
    /// string (<c>from:someone@example.com</c>, <c>invoice AND march</c>); quotes and backslashes
    /// inside it are escaped. KQL can only widen or narrow the matches within the one folder being
    /// read, so the text needs no other screening.
    /// </summary>
    public static string SearchClause(string search)
    {
        var escaped = search.Trim().Replace(@"\", @"\\").Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }

    public async Task<string> SendEmailAsync(string toAddress, string subject, string body)
    {
        var message = new Message
        {
            Subject = subject,
            Body = new ItemBody
            {
                ContentType = BodyType.Text,
                Content = body
            },
            ToRecipients =
            [
                new Recipient
                {
                    EmailAddress = new EmailAddress
                    {
                        Address = toAddress
                    }
                }
            ]
        };

        await _appClient.Users[_entraUserId].SendMail.PostAsync(new Microsoft.Graph.Users.Item.SendMail.SendMailPostRequestBody
        {
            Message = message,
            SaveToSentItems = true
        });

        return $"Email sent successfully to {toAddress}";
    }

    #endregion

    #region OneDrive

    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";

    // The app holds Files.SelectedOperations.Selected with a write grant on this one folder, not
    // tenant-wide file access: every call below is inside it, and anything else is a 403.
    private static (string DriveId, string FolderId) OneDriveFolder()
    {
        var driveId = Environment.GetEnvironmentVariable("GRAPH_ONEDRIVE_DRIVE_ID");
        var folderId = Environment.GetEnvironmentVariable("GRAPH_ONEDRIVE_FOLDER_ID");
        if (string.IsNullOrWhiteSpace(driveId) || string.IsNullOrWhiteSpace(folderId))
            throw new InvalidOperationException("GRAPH_ONEDRIVE_DRIVE_ID and GRAPH_ONEDRIVE_FOLDER_ID are not configured.");
        return (driveId, folderId);
    }

    /// <summary>
    /// Saves an email as a PDF in the OneDrive folder, named for its local received time and
    /// subject. See <see cref="RenderEmailAsPdfAsync"/>.
    /// </summary>
    public async Task<SavedEmailModel> SaveEmailAsPdfAsync(string messageId, TimeZoneInfo timeZone, bool includeLinkedImages)
    {
        var email = await RenderEmailAsPdfAsync(messageId, timeZone, includeLinkedImages);
        var (driveId, folderId) = OneDriveFolder();
        var fileName = EmailPdf.FileName(email.Subject, email.ReceivedDateTime, timeZone);
        var saved = await UploadToFolderAsync(driveId, folderId, fileName, email.Pdf);
        return new SavedEmailModel
        {
            Saved = true,
            FileName = saved.Name ?? fileName,
            WebUrl = saved.WebUrl ?? string.Empty,
            LinkedImages = email.LinkedImages,
            ImagesMissing = email.ImagesMissing,
        };
    }

    /// <summary>
    /// How many images the email links to on other servers (tracking pixels aside). Downloading
    /// them tells the sender the email was opened, so the owner is asked first.
    /// </summary>
    public async Task<int> GetLinkedImageCountAsync(string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        var message = await _appClient.Users[_entraUserId].Messages[messageId].GetAsync(config =>
        {
            config.QueryParameters.Select = ["body"];
            config.Headers.Add("Prefer", "outlook.body-content-type=\"html\"");
        }) ?? throw new InvalidOperationException("Graph returned no email for that id.");
        return EmailPdf.ImageSources(message.Body?.Content ?? string.Empty).Count(EmailPdf.IsLinked);
    }

    /// <summary>
    /// Renders an email as a PDF: <see cref="EmailPdf.RenderHtml"/> writes it as one HTML page with
    /// its images inside it, the page goes into the OneDrive folder as a temporary file, Graph
    /// converts it, and the file is deleted. Graph's converter shows neither an .eml's embedded
    /// images nor any linked ones (tried 2026-10-22), so embedded images are always written in, and
    /// linked ones only with <paramref name="includeLinkedImages"/>, the owner's yes.
    /// </summary>
    public async Task<EmailPdfModel> RenderEmailAsPdfAsync(string messageId, TimeZoneInfo timeZone, bool includeLinkedImages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        var (driveId, folderId) = OneDriveFolder();
        var message = _appClient.Users[_entraUserId].Messages[messageId];

        var email = await message.GetAsync(config =>
        {
            config.QueryParameters.Select = ["subject", "from", "toRecipients", "ccRecipients", "sentDateTime", "receivedDateTime", "body"];
            // Plain-text emails come back as HTML too, so there is one rendering path.
            config.Headers.Add("Prefer", "outlook.body-content-type=\"html\"");
        }) ?? throw new InvalidOperationException("Graph returned no email for that id.");

        // No content: without $select, file attachments come back with it.
        var attachments = (await message.Attachments.GetAsync(config =>
            config.QueryParameters.Select = ["id", "name", "isInline", "contentType", "size"]))?.Value
            ?? throw new InvalidOperationException("Graph returned no attachment list for the email.");

        var html = EmailPdf.RenderHtml(email,
            attachments.Where(a => a.IsInline != true).Select(a => a.Name ?? "(unnamed)").ToArray(), timeZone);
        if (EmailPdf.Utf8WithBom(html).Length > EmailPdf.ConversionLimitBytes)
            throw new InvalidOperationException("This email is over the 2 MB PDF conversion limit even without its images and attachments, so it can't be made into a PDF.");

        var sources = EmailPdf.ImageSources(html);
        var images = new Dictionary<string, MediaItem>();
        if (sources.Any(EmailPdf.IsEmbedded))
        {
            foreach (var inline in attachments.Where(a => a.IsInline == true && a.Id is not null
                && a.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true
                && a.Size <= EmailPdf.ConversionLimitBytes))
            {
                if (await message.Attachments[inline.Id].GetAsync() is FileAttachment { ContentId: { } contentId, ContentBytes: { } bytes })
                    images[EmailPdf.EmbeddedSource(contentId)] = new MediaItem(bytes, inline.ContentType!.ToLowerInvariant());
            }
        }
        if (includeLinkedImages)
        {
            foreach (var (url, image) in await _imageFetcher.FetchAsync(sources.Where(EmailPdf.IsLinked)))
                images[url] = image;
        }

        var (page, written) = EmailPdf.EmbedImages(html, images, EmailPdf.ConversionLimitBytes);
        var wanted = sources.Count(s => EmailPdf.IsEmbedded(s) || (includeLinkedImages && EmailPdf.IsLinked(s)));

        var temporary = await UploadToFolderAsync(driveId, folderId, $"ancela-{Guid.NewGuid():N}.html", EmailPdf.Utf8WithBom(page));
        var temporaryId = temporary.Id ?? throw new InvalidOperationException("Graph returned no id for the uploaded email.");
        try
        {
            return new EmailPdfModel
            {
                Subject = email.Subject,
                ReceivedDateTime = email.ReceivedDateTime ?? DateTimeOffset.UtcNow,
                Pdf = await ConvertToPdfAsync(driveId, temporaryId),
                LinkedImages = sources.Count(EmailPdf.IsLinked),
                ImagesMissing = wanted - written,
            };
        }
        finally
        {
            // Best effort: a leftover "ancela-….html" in the owner's own folder is untidy, not a
            // leak, and throwing here would report a conversion that worked as a failure.
            try { await _appClient.Drives[driveId].Items[temporaryId].DeleteAsync(); }
            catch (Exception) { }
        }
    }

    /// <summary>The granted folder's name. Proves the per-folder grant still exists.</summary>
    public async Task<string> GetOneDriveFolderNameAsync()
    {
        var (driveId, folderId) = OneDriveFolder();
        var folder = await _appClient.Drives[driveId].Items[folderId].GetAsync(config => config.QueryParameters.Select = ["name"]);
        return folder?.Name ?? string.Empty;
    }

    private async Task<byte[]> ConvertToPdfAsync(string driveId, string itemId)
    {
        // Graph answers with a 302 to a short-lived pre-authenticated URL on another host. The SDK
        // follows it, and its redirect handler drops the Authorization header on a cross-host hop
        // (Kiota's RedirectHandlerOption.DefaultScrubSensitiveHeaders), so the token stays with
        // Graph. OneDriveLiveTests pins that behaviour.
        await using var stream = await _appClient.Drives[driveId].Items[itemId].Content
            .GetAsync(config => config.QueryParameters.Format = "pdf")
            ?? throw new InvalidOperationException("Graph returned no PDF.");
        using var pdf = new MemoryStream();
        await stream.CopyToAsync(pdf);

        var bytes = pdf.ToArray();
        if (!bytes.AsSpan().StartsWith("%PDF-"u8))
            throw new InvalidOperationException("Graph's PDF conversion did not return a PDF.");
        return bytes;
    }

    private async Task<DriveItem> UploadToFolderAsync(string driveId, string folderId, string fileName, byte[] content)
    {
        // Addressed by path under the folder, escaped here: the SDK's ItemWithPath doesn't
        // percent-encode, and fileName carries an email subject. An existing name is replaced.
        var url = $"{GraphBaseUrl}/drives/{Uri.EscapeDataString(driveId)}/items/{Uri.EscapeDataString(folderId)}"
            + $":/{Uri.EscapeDataString(fileName)}:/content";
        using var stream = new MemoryStream(content);
        return await _appClient.Drives[driveId].Items[folderId].Content.WithUrl(url).PutAsync(stream)
            ?? throw new InvalidOperationException("Graph returned no item for the upload.");
    }

    #endregion

    #region Contacts

    public async Task<ContactModel[]> GetUserContactsAsync(int maxResults = 50)
    {
        var contacts = await _appClient.Users[_entraUserId].Contacts.GetAsync((config) =>
        {
            // Request specific properties.
            config.QueryParameters.Select = ["displayName", "emailAddresses", "mobilePhone", "businessPhones", "companyName", "jobTitle"];
            // Get contacts up to the specified limit.
            config.QueryParameters.Top = maxResults;
            // Sort by display name.
            config.QueryParameters.Orderby = ["displayName"];
        });

        if (contacts?.Value == null)
            throw new InvalidOperationException("Graph returned no response for the contact list.");

        return contacts.Value
            .Select(c => new ContactModel
            {
                DisplayName = c.DisplayName ?? string.Empty,
                EmailAddresses = c.EmailAddresses?.Select(e => e.Address ?? string.Empty).Where(e => !string.IsNullOrEmpty(e)).ToArray() ?? [],
                MobilePhone = c.MobilePhone ?? string.Empty,
                BusinessPhones = c.BusinessPhones?.Where(p => !string.IsNullOrEmpty(p)).ToArray() ?? [],
                CompanyName = c.CompanyName ?? string.Empty,
                JobTitle = c.JobTitle ?? string.Empty
            }).ToArray();
    }

    public async Task<ContactModel?> GetUserContactByNameAsync(string name)
    {
        var searchTerm = name.Trim();
        // Escape single quotes per OData (double them) so names like "O'Brien" don't
        // malform the filter, and so caller-supplied text can't inject filter syntax.
        var filterTerm = searchTerm.Replace("'", "''");

        var contacts = await _appClient.Users[_entraUserId].Contacts.GetAsync((config) =>
        {
            // Request specific properties.
            config.QueryParameters.Select = ["displayName", "emailAddresses", "mobilePhone", "businessPhones", "companyName", "jobTitle", "givenName", "surname"];
            // Filter by display name, given name, or surname containing the search term.
            config.QueryParameters.Filter = $"startswith(displayName,'{filterTerm}') or startswith(givenName,'{filterTerm}') or startswith(surname,'{filterTerm}')";
            // Get top 10 matches.
            config.QueryParameters.Top = 10;
        });

        // No response is a failure; null is kept for "no contact by that name".
        if (contacts?.Value == null)
            throw new InvalidOperationException("Graph returned no response for the contact search.");
        if (contacts.Value.Count == 0)
            return null;

        // Return the first match, or the best match if display name contains the exact search term.
        var exactMatch = contacts.Value.FirstOrDefault(c =>
            c.DisplayName?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) == true);

        var contact = exactMatch ?? contacts.Value.First();

        return new ContactModel
        {
            DisplayName = contact.DisplayName ?? string.Empty,
            EmailAddresses = contact.EmailAddresses?.Select(e => e.Address ?? string.Empty).Where(e => !string.IsNullOrEmpty(e)).ToArray() ?? [],
            MobilePhone = contact.MobilePhone ?? string.Empty,
            BusinessPhones = contact.BusinessPhones?.Where(p => !string.IsNullOrEmpty(p)).ToArray() ?? [],
            CompanyName = contact.CompanyName ?? string.Empty,
            JobTitle = contact.JobTitle ?? string.Empty
        };
    }

    #endregion
}
