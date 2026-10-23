using System.Globalization;
using System.Text.RegularExpressions;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using MessageItemRequestBuilder = Microsoft.Graph.Users.Item.Messages.Item.MessageItemRequestBuilder;

namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

public interface IGraphClient
{
    Task<EventModel[]> GetUserEventsAsync(DateTimeOffset start, DateTimeOffset end);
    Task<EventModel> CreateEventAsync(string subject, string start, string end, string? location = null, string? body = null, bool isAllDay = false);
    Task<EmailModel[]> GetUserEmailsAsync(MailFolder folder = MailFolder.Inbox, string? search = null);
    Task<EmailContentModel> GetEmailAsync(string messageId);
    Task<string> SendEmailAsync(string toAddress, string subject, string body);
    Task<SavedEmailModel> SaveEmailAsPdfAsync(string messageId, TimeZoneInfo timeZone);
    Task<EmailPdfModel> RenderEmailAsPdfAsync(string messageId, TimeZoneInfo timeZone);
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

    public GraphClient()
    {
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

        var message = await _appClient.Users[_entraUserId].Messages[messageId].GetAsync(config =>
        {
            config.QueryParameters.Select = ["id", "subject", "from", "toRecipients", "ccRecipients", "receivedDateTime", "body", "hasAttachments", "parentFolderId"];
            // Graph converts an HTML body to text, which is all the model needs and far shorter.
            config.Headers.Add("Prefer", "outlook.body-content-type=\"text\"");
        }) ?? throw new InvalidOperationException("Graph returned no email for that id.");

        // The same folders get_recent_emails reads. Its results are where ids come from, but an id
        // can also come from anywhere else the model has read, such as another email's text.
        if (!(await ReadableFolderIdsAsync()).Contains(message.ParentFolderId))
            throw new InvalidOperationException("That email is not in the Inbox, Sent or Deleted folder, the only ones Ancela reads.");

        return ToEmailContent(message);
    }

    private async Task<string[]> ReadableFolderIdsAsync() =>
        _readableFolderIds ??= await Task.WhenAll(Enum.GetValues<MailFolder>().Select(async folder =>
            (await _appClient.Users[_entraUserId].MailFolders[WellKnownName(folder)].GetAsync(config => config.QueryParameters.Select = ["id"]))?.Id
            ?? throw new InvalidOperationException($"Graph returned no id for the {folder} folder.")));

    /// <summary>
    /// Maps a message fetched with a text body, tidied and cut to <see cref="EmailBodyCap"/>. Text
    /// converted from HTML carries runs of blank lines and trailing spaces that cost tokens.
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
            HasAttachments = message.HasAttachments ?? false,
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
    public async Task<SavedEmailModel> SaveEmailAsPdfAsync(string messageId, TimeZoneInfo timeZone)
    {
        var email = await RenderEmailAsPdfAsync(messageId, timeZone);
        var (driveId, folderId) = OneDriveFolder();
        var fileName = EmailPdf.FileName(email.Subject, email.ReceivedDateTime, timeZone);
        var saved = await UploadToFolderAsync(driveId, folderId, fileName, email.Pdf);
        return new SavedEmailModel
        {
            FileName = saved.Name ?? fileName,
            WebUrl = saved.WebUrl ?? string.Empty,
            BodyOnly = email.BodyOnly,
            OmittedAttachments = email.OmittedAttachments,
        };
    }

    /// <summary>
    /// Renders a whole email as a PDF: the raw message goes into the OneDrive folder as a temporary
    /// .eml, Graph converts it, and the .eml is deleted. A message over the 2 MB conversion limit is
    /// rendered from its headers and body alone.
    /// </summary>
    public async Task<EmailPdfModel> RenderEmailAsPdfAsync(string messageId, TimeZoneInfo timeZone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        var (driveId, folderId) = OneDriveFolder();
        var message = _appClient.Users[_entraUserId].Messages[messageId];

        var summary = await message.GetAsync(config => config.QueryParameters.Select = ["subject", "receivedDateTime"])
            ?? throw new InvalidOperationException("Graph returned no email for that id.");

        // The raw MIME message is the best source: Graph renders an .eml with a From/Sent/To/Subject
        // block and the full HTML body. Attachments are inside it and count toward the limit, so
        // reading stops one chunk past the limit rather than downloading a large attachment.
        byte[]? mime;
        await using (var raw = await message.Content.GetAsync()
            ?? throw new InvalidOperationException("Graph returned no content for the email."))
        {
            mime = await ReadAtMostAsync(raw, EmailPdf.ConversionLimitBytes);
        }

        var bodyOnly = mime is null;
        var (source, omitted) = mime is not null ? (mime, []) : await RenderBodyOnlyAsync(message, timeZone);

        var temporary = await UploadToFolderAsync(driveId, folderId,
            $"ancela-{Guid.NewGuid():N}{(bodyOnly ? ".html" : ".eml")}", source);
        var temporaryId = temporary.Id ?? throw new InvalidOperationException("Graph returned no id for the uploaded email.");
        try
        {
            return new EmailPdfModel
            {
                Subject = summary.Subject,
                ReceivedDateTime = summary.ReceivedDateTime ?? DateTimeOffset.UtcNow,
                Pdf = await ConvertToPdfAsync(driveId, temporaryId),
                BodyOnly = bodyOnly,
                OmittedAttachments = omitted,
            };
        }
        finally
        {
            // Best effort: a leftover "ancela-….eml" in the owner's own folder is untidy, not a
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

    /// <summary>The whole stream, or null once it passes <paramref name="limit"/> bytes.</summary>
    private static async Task<byte[]?> ReadAtMostAsync(Stream stream, int limit)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + read > limit)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static async Task<(byte[] Html, string[] Omitted)> RenderBodyOnlyAsync(MessageItemRequestBuilder message, TimeZoneInfo timeZone)
    {
        var full = await message.GetAsync(config =>
        {
            config.QueryParameters.Select = ["subject", "from", "toRecipients", "ccRecipients", "sentDateTime", "body"];
            // Plain-text emails come back as HTML too, so there is one rendering path.
            config.Headers.Add("Prefer", "outlook.body-content-type=\"html\"");
        }) ?? throw new InvalidOperationException("Graph returned no email for that id.");

        // Names only: without $select, file attachments come back with their content.
        var attachments = await message.Attachments.GetAsync(config => config.QueryParameters.Select = ["name", "isInline"]);
        var omitted = attachments?.Value?
            .Where(a => a.IsInline != true)
            .Select(a => a.Name ?? "(unnamed)")
            .ToArray() ?? [];

        var html = EmailPdf.Utf8WithBom(EmailPdf.RenderBodyOnlyHtml(full, omitted, timeZone));
        if (html.Length > EmailPdf.ConversionLimitBytes)
            throw new InvalidOperationException("This email is over the 2 MB PDF conversion limit even without its attachments, so it can't be saved as a PDF.");
        return (html, omitted);
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
