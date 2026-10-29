using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using Ancela.Agent.SemanticKernel.Plugins.IpLookupPlugin;
using Ancela.Agent.SemanticKernel.Plugins.IpLookupPlugin.Models;
using Ancela.Agent.SemanticKernel.Plugins.RemarkablePlugin;
using Ancela.Agent.SemanticKernel.Plugins.ScheduledTaskPlugin.Models;
using Ancela.Agent.Services;
using FluentAssertions;
using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// Integration tests verifying that calendar and email prompts trigger the correct
/// IGraphClient function calls via the AI's function calling capability. These hit a live
/// model, so they are gated under the "Integration" category and retried (via
/// <see cref="AgentTestBase.SendUntilAsync"/>) to absorb non-deterministic tool selection.
/// </summary>
[Trait("Category", "Integration")]
public class AgentGraphTests : AgentTestBase
{
    [Fact]
    public async Task GetCalendarEvents_WhenUserAsksAboutToday_QueriesTodaysDateRange()
    {
        // Arrange. "Today" must be computed in the *user's* timezone — the model is told
        // the user's timezone and resolves "today" there, so comparing against UTC fails
        // near the day boundary (e.g. evening in Pacific, where UTC has already rolled over)
        // even when the model is correct.
        var userTz = TimeZoneInfo.FindSystemTimeZoneById(TestUserTimeZone);
        var todayStart = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, userTz).Date;
        var todayEnd = todayStart.AddDays(1);

        SetupCalendarEvents(
            CreateEvent("Team meeting", todayStart.AddHours(10), todayStart.AddHours(11)),
            CreateEvent("Lunch", todayStart.AddHours(12), todayStart.AddHours(13)));

        // Act + Assert: the model must translate "today" into the correct date range.
        await SendUntilAsync("what's on my calendar today?", () =>
            MockGraphClient.Verify(
                g => g.GetUserEventsAsync(
                    It.Is<DateTimeOffset>(d => d.Date == todayStart.Date),
                    It.Is<DateTimeOffset>(d => d.Date == todayEnd.Date || d.Date == todayStart.Date)),
                Times.AtLeastOnce));
    }

    [Fact]
    public async Task GetCalendarEvents_WhenNoEvents_StillReturnsAResponse()
    {
        SetupCalendarEvents(); // Empty events

        var response = await SendUntilAsync("what meetings do I have today?", () =>
            MockGraphClient.Verify(
                g => g.GetUserEventsAsync(
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<DateTimeOffset>()),
                Times.AtLeastOnce));

        Assert.NotNull(response);
    }

    [Fact]
    public async Task SaveEmail_WhenTheNamedEmailIsOlderThanTheNewest50_FindsItBySearch()
    {
        // The production bug: the email the owner named was older than the 50 newest, which is all
        // a plain listing returns, and the model saved the newest email in its place.
        var named = CreateEmail("named", "noreply@newsletter.example", "An update to your account number", hoursAgo: 28 * 24);
        SetupEmails(search => search?.Contains("newsletter", StringComparison.OrdinalIgnoreCase) == true ? [named] : NewestFifty());
        SetupSave();

        await SendUntilAsync("save the email from noreply@newsletter.example to OneDrive", () =>
            MockGraphClient.Verify(g => g.SaveEmailAsPdfAsync("named", It.IsAny<TimeZoneInfo>(), It.IsAny<bool>()), Times.AtLeastOnce));

        MockGraphClient.Verify(g => g.SaveEmailAsPdfAsync(It.Is<string>(id => id != "named"), It.IsAny<TimeZoneInfo>(), It.IsAny<bool>()),
            Times.Never, "no other email may be saved in its place");
    }

    [Fact]
    public async Task SaveEmail_WhenTheNamedEmailIsNowhere_SavesNothing()
    {
        SetupEmails(search => search is null ? NewestFifty() : []);
        SetupSave();

        await SendUntilAsync("save the email from noreply@newsletter.example to OneDrive", () =>
            MockGraphClient.Verify(g => g.GetUserEmailsAsync(It.IsAny<MailFolder>(), It.IsNotNull<string>()),
                Times.AtLeastOnce, "the model has to search before concluding the email isn't there"));

        MockGraphClient.Verify(g => g.SaveEmailAsPdfAsync(It.IsAny<string>(), It.IsAny<TimeZoneInfo>(), It.IsAny<bool>()),
            Times.Never, "when the named email can't be found, nothing may be saved in its place");
    }

    [Fact]
    public async Task SendEmailsToRemarkable_SendsEachWholeEmail_NotTextTheModelWrote()
    {
        // The production bug: asked to send two emails to the reMarkable, the model had only their
        // 255-character previews and passed send_to_remarkable a one-line summary of each.
        var remarkable = new Mock<IRemarkableService>();
        remarkable
            .Setup(r => r.SendPdfAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("doc-id");
        ExtraPlugins.Add(new RemarkablePlugin(remarkable.Object, MockGraphClient.Object));
        EmailModel[] fromTheOffice =
        [
            CreateEmail("trip", "office@school.example", "Field trip permission form", hoursAgo: 3),
            CreateEmail("lunch", "office@school.example", "Lunch menu changes", hoursAgo: 5),
        ];
        SetupEmails(search => search is null ? [.. fromTheOffice, .. NewestFifty()[..48]] : fromTheOffice);
        MockGraphClient
            .Setup(g => g.RenderEmailAsPdfAsync(It.IsAny<string>(), It.IsAny<TimeZoneInfo>(), It.IsAny<bool>()))
            .ReturnsAsync((string id, TimeZoneInfo _, bool _) => new EmailPdfModel
            {
                Subject = id,
                ReceivedDateTime = DateTimeOffset.UtcNow,
                Pdf = "%PDF-1.7"u8.ToArray(),
                LinkedImages = 0,
                ImagesMissing = 0,
            });

        await SendUntilAsync("send the two emails from the school office to my reMarkable", () =>
        {
            MockGraphClient.Verify(g => g.RenderEmailAsPdfAsync("trip", It.IsAny<TimeZoneInfo>(), It.IsAny<bool>()), Times.AtLeastOnce);
            MockGraphClient.Verify(g => g.RenderEmailAsPdfAsync("lunch", It.IsAny<TimeZoneInfo>(), It.IsAny<bool>()), Times.AtLeastOnce);
        });

        remarkable.Verify(r => r.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "an email sent as text is whatever the model wrote from a 255-character preview");
    }

    [Fact]
    public async Task SendEmailWithLinkedImages_AsksFirst_ThenSendsWithTheAnswer()
    {
        // Downloading the images an email links to tells the sender it was opened, so nothing is
        // downloaded until the user says yes, which comes in their next message.
        var remarkable = new Mock<IRemarkableService>();
        remarkable
            .Setup(r => r.SendPdfAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("doc-id");
        ExtraPlugins.Add(new RemarkablePlugin(remarkable.Object, MockGraphClient.Object));
        var history = UseRecordedHistory();
        SetupEmails(_ => [CreateEmail("catalog", "news@shop.example", "Fall catalog", hoursAgo: 2)]);
        MockGraphClient.Setup(g => g.GetLinkedImageCountAsync("catalog")).ReturnsAsync(12);
        var renders = new List<bool>();
        MockGraphClient
            .Setup(g => g.RenderEmailAsPdfAsync(It.IsAny<string>(), It.IsAny<TimeZoneInfo>(), It.IsAny<bool>()))
            .Callback<string, TimeZoneInfo, bool>((_, _, include) => renders.Add(include))
            .ReturnsAsync((string id, TimeZoneInfo _, bool include) => new EmailPdfModel
            {
                Subject = "Fall catalog",
                ReceivedDateTime = DateTimeOffset.UtcNow,
                Pdf = "%PDF-1.7"u8.ToArray(),
                LinkedImages = 12,
                ImagesMissing = 0,
            });

        await SendUntilAsync("send the fall catalog email from the shop to my reMarkable", reply =>
        {
            MockGraphClient.Verify(g => g.GetLinkedImageCountAsync("catalog"), Times.AtLeastOnce);
            renders.Should().BeEmpty(because: $"nothing is sent before the user answers; the reply was: {reply}");
            reply.Should().ContainEquivalentOf("image");
        }, reset: () => { history.Clear(); renders.Clear(); });

        var afterFirst = history.ToList();
        await SendUntilAsync("Yes, include the images.", () =>
            renders.Should().Equal([true], because: "the yes downloads the images, and the email is sent once"),
            reset: () => { history.Clear(); history.AddRange(afterFirst); renders.Clear(); });
    }

    [Fact]
    public async Task AskingWhatAnEmailSays_ReadsItInFull()
    {
        // get_recent_emails has only the first 255 characters of each email. A question about a step
        // past them can only be answered by reading the email.
        var steps = string.Join("\n", Enumerable.Range(1, 6).Select(i => $"Step {i}: complete part {i} of the setup form in the app.")) +
            "\nStep 7: turn on two-factor authentication under Security, then call us to confirm.";
        var setup = new EmailModel
        {
            Id = "setup", From = "support@bank.example", Subject = "Finish setting up your account",
            ReceivedDateTime = DateTimeOffset.UtcNow.AddHours(-4), BodyPreview = steps[..255], IsRead = false,
        };
        SetupEmails(_ => [setup]);
        MockGraphClient
            .Setup(g => g.GetEmailAsync("setup"))
            .ReturnsAsync(new EmailContentModel
            {
                Id = "setup", Subject = setup.Subject, From = setup.From, To = [], Cc = [],
                ReceivedDateTime = setup.ReceivedDateTime, Body = steps, Truncated = false, Attachments = [],
            });

        await SendUntilAsync("what's the last step in the account setup email from my bank?", reply =>
        {
            MockGraphClient.Verify(g => g.GetEmailAsync("setup"), Times.AtLeastOnce);
            reply.Should().ContainEquivalentOf("two-factor", because: $"step 7 is only in the full email; the reply was: {reply}");
        });
    }

    [Fact]
    public async Task DmarcSummary_CombinesTheEmailAndIpTools_ThenTrashesTheReports()
    {
        // No tool knows what DMARC is. A scheduled summary has to find the reports, list each one's
        // attachments, read them, look up who holds each rejected address, and mark read and trash
        // what it read: six general tools the model combines by itself, with reasoning off.
        var (registries, markedRead) = SetupDmarcReports();

        var reply = await PerformUntilAsync(DmarcSummaryTask(), reply =>
            reply.Contains("DigitalOcean", StringComparison.OrdinalIgnoreCase) && reply.Contains("Mailchimp", StringComparison.OrdinalIgnoreCase));

        MockGraphClient.Verify(g => g.GetAttachmentAsync("google", It.IsAny<string>()), Times.AtLeastOnce);
        MockGraphClient.Verify(g => g.GetAttachmentAsync("yahoo", It.IsAny<string>()), Times.AtLeastOnce);
        registries.Verify(r => r.LookupAsync("203.0.113.50"), Times.AtLeastOnce);
        registries.Verify(r => r.LookupAsync("198.51.100.7"), Times.AtLeastOnce);
        reply.Should().ContainEquivalentOf("DigitalOcean", because: $"203.0.113.50 was rejected; the reply was: {reply}")
            .And.ContainEquivalentOf("Mailchimp", because: "198.51.100.7 was rejected")
            .And.Contain("6").And.Contain("3");
        MockGraphClient.Verify(g => g.MoveToDeletedItemsAsync("google"), Times.AtLeastOnce);
        MockGraphClient.Verify(g => g.MoveToDeletedItemsAsync("yahoo"), Times.AtLeastOnce);
        MockGraphClient.Verify(g => g.MoveToDeletedItemsAsync("sale"), Times.Never, "only the reports were processed");
        markedRead.Should().BeEquivalentTo(["google", "yahoo"], because: "each report is marked read before the move changes its id");
    }

    [Fact]
    public async Task DmarcSummary_LeavesAReportItCouldNotRead()
    {
        // "After successful processing": Yahoo's attachment can't be read, so that report stays in the
        // inbox for next time, and the summary says so rather than leaving it out.
        var (_, markedRead) = SetupDmarcReports(yahooAttachmentFails: true);

        var reply = await PerformUntilAsync(DmarcSummaryTask(), reply =>
            reply.Contains("DigitalOcean", StringComparison.OrdinalIgnoreCase) && reply.Contains("Yahoo", StringComparison.OrdinalIgnoreCase));

        MockGraphClient.Verify(g => g.MoveToDeletedItemsAsync("google"), Times.AtLeastOnce);
        MockGraphClient.Verify(g => g.MoveToDeletedItemsAsync("yahoo"), Times.Never, "its report was never read");
        markedRead.Should().NotContain("yahoo", because: "an unread report stays unread, for the owner to notice");
        reply.Should().ContainEquivalentOf("Yahoo", because: $"the summary must say a report couldn't be read; the reply was: {reply}");
    }

    private ScheduledTask DmarcSummaryTask() => new()
    {
        Id = Guid.NewGuid(),
        UserPhoneNumber = UserPhoneNumber,
        AgentPhoneNumber = AgentPhoneNumber,
        Description = "Summarize yesterday's DMARC reports: which senders were rejected, how many messages, and who each "
            + "rejected IP address belongs to. Then mark each report you processed as read and move it to the trash.",
        TimeOfDay = "08:00",
        DaysOfWeek = [0, 1, 2, 3, 4, 5, 6],
    };

    /// <summary>
    /// Runs the task up to three times, until <paramref name="passes"/> accepts the reply. Mock calls
    /// add up across attempts, so the caller verifies with AtLeastOnce or Never.
    /// </summary>
    private async Task<string> PerformUntilAsync(ScheduledTask task, Func<string, bool> passes)
    {
        var reply = string.Empty;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            reply = await Agent.PerformScheduledTask(task, TestUser);
            if (passes(reply))
                break;
        }
        return reply;
    }

    /// <summary>
    /// Google's and Yahoo's reports for yesterday, a newsletter that isn't one, and the registries'
    /// answers for the rejected addresses: a spoofer on DigitalOcean (6) and Mailchimp (3). Also the
    /// emails marked read: as in Graph, a moved email's old id no longer works.
    /// </summary>
    private (Mock<IRdapClient> Registries, HashSet<string> MarkedRead) SetupDmarcReports(bool yahooAttachmentFails = false)
    {
        var yesterday = DateTimeOffset.UtcNow.Date.AddDays(-1);
        var (begin, end) = (new DateTimeOffset(yesterday).ToUnixTimeSeconds(), new DateTimeOffset(yesterday).ToUnixTimeSeconds() + 86399);
        string Report(string org, params (string Ip, int Count, string Disposition, string Result)[] rows) =>
            $"<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\n<feedback>\n"
            + $"  <report_metadata><org_name>{org}</org_name><date_range><begin>{begin}</begin><end>{end}</end></date_range></report_metadata>\n"
            + "  <policy_published><domain>example.com</domain><p>reject</p></policy_published>\n"
            + string.Concat(rows.Select(r =>
                $"  <record><row><source_ip>{r.Ip}</source_ip><count>{r.Count}</count><policy_evaluated><disposition>{r.Disposition}</disposition>"
                + $"<dkim>{r.Result}</dkim><spf>{r.Result}</spf></policy_evaluated></row><identifiers><header_from>example.com</header_from></identifiers></record>\n"))
            + "</feedback>\n";

        var google = CreateEmail("google", "noreply-dmarc-support@google.com", "Report domain: example.com Submitter: google.com Report-ID: 4931150279366017725", hoursAgo: 5);
        var yahoo = CreateEmail("yahoo", "noreply@dmarc.yahoo.com", "Report Domain: example.com Submitter: yahoo.com Report-ID: <1700000000.123456>", hoursAgo: 3);
        EmailModel[] inbox = [CreateEmail("sale", "news@shop.example", "Fall sale ends tonight", hoursAgo: 1), yahoo, google];
        SetupEmails(_ => inbox);
        SetupReport(google, body: "", $"google.com!example.com!{begin}!{end}.zip", Report("google.com",
            ("209.85.220.41", 14, "none", "pass"), ("203.0.113.50", 6, "reject", "fail")));
        SetupReport(yahoo, body: "This is an aggregate DMARC report from Yahoo for your domain \"example.com\".", $"yahoo.com!example.com!{begin}!{end}.xml.gz",
            Report("Yahoo", ("198.51.100.7", 3, "reject", "fail"), ("209.85.220.69", 5, "none", "pass")));
        if (yahooAttachmentFails)
            MockGraphClient.Setup(g => g.GetAttachmentAsync("yahoo", It.IsAny<string>()))
                .ThrowsAsync(new InvalidDataException("The archive entry was compressed using an unsupported compression method."));
        var (moved, markedRead) = (new HashSet<string>(), new HashSet<string>());
        MockGraphClient.Setup(g => g.MoveToDeletedItemsAsync(It.IsAny<string>())).ReturnsAsync((string id) =>
        {
            var email = inbox.Single(e => e.Id == id);
            moved.Add(id);
            return $"Moved to Deleted Items: \"{email.Subject}\" from {email.From}.";
        });
        MockGraphClient.Setup(g => g.MarkReadAsync(It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync((string id, bool read) =>
        {
            if (moved.Contains(id))
                throw new InvalidOperationException("The specified object was not found in the store.");
            var email = inbox.Single(e => e.Id == id);
            if (read)
                markedRead.Add(id);
            return $"Marked as {(read ? "read" : "unread")}: \"{email.Subject}\" from {email.From}.";
        });

        var registries = new Mock<IRdapClient>();
        registries.Setup(r => r.LookupAsync(It.IsAny<string>())).ReturnsAsync((string ip) => ip switch
        {
            "203.0.113.50" => Registration(ip, "DIGITALOCEAN-203-0-113-0", "DigitalOcean, LLC"),
            "198.51.100.7" => Registration(ip, "RSG-DELIVERY", "MailChimp"),
            _ => Registration(ip, "GOOGLE", "Google LLC"),
        });
        ExtraPlugins.Add(new IpLookupPlugin(registries.Object));
        return (registries, markedRead);
    }

    /// <summary>
    /// A report email as read_email and read_attachment give it: the data is only in the attachment,
    /// and a name other than the one listed fails as it does against Graph.
    /// </summary>
    private void SetupReport(EmailModel email, string body, string attachment, string xml)
    {
        MockGraphClient.Setup(g => g.GetEmailAsync(email.Id)).ReturnsAsync(new EmailContentModel
        {
            Id = email.Id, Subject = email.Subject, From = email.From, To = ["dmarc@example.com"], Cc = [],
            ReceivedDateTime = email.ReceivedDateTime, Body = body, Truncated = false, Attachments = [attachment],
        });
        var file = attachment.EndsWith(".gz") ? attachment[..^3] : attachment.Replace(".zip", ".xml");
        MockGraphClient.Setup(g => g.GetAttachmentAsync(email.Id, It.IsAny<string>())).ReturnsAsync((string _, string name) =>
            name == attachment
                ? new AttachmentContentModel { Name = name, Files = [new AttachmentFileModel { Name = file, Text = xml, Truncated = false }], NotRead = [] }
                : throw new NotInEmailException($"That email has no attachment named '{name}'. Its attachments: {attachment}."));
    }

    private static IpRegistration Registration(string ip, string network, string holder) =>
        new(ip, network, null, "US", [], [new RegistryContact(holder, ["registrant"])]);

    [Fact]
    public async Task ReplyToAnEmail_ConfirmsFirst_ThenSendsInTheNextMessage()
    {
        // Reading the email switches send_email off for the rest of that message, because the
        // email could be what asked for the send. So a reply takes two messages: Ancela says what
        // it would send, the user confirms, and the confirming message, having read nothing, sends.
        var history = UseRecordedHistory();
        SetupEmails(_ => [CreateEmail("thursday", "bob@example.com", "Can you make Thursday at 3pm?", hoursAgo: 2)]);
        MockGraphClient
            .Setup(g => g.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync("Email sent.");

        await SendUntilAsync("Reply to Bob's email saying yes, Thursday at 3 works for me.", () =>
            MockGraphClient.Verify(
                g => g.GetUserEmailsAsync(It.IsAny<MailFolder>(), It.IsAny<string?>()),
                Times.AtLeastOnce),
            reset: history.Clear);
        MockGraphClient.Verify(g => g.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never, "nothing may be sent in the message that read the email");

        var afterFirst = history.ToList();
        await SendUntilAsync("Yes, send it.", () =>
            MockGraphClient.Verify(g => g.SendEmailAsync("bob@example.com", It.IsAny<string>(), It.IsAny<string>()),
                Times.AtLeastOnce, "the confirming message sends the reply it was shown"),
            reset: () => { history.Clear(); history.AddRange(afterFirst); });
    }

    /// <summary>
    /// Keeps the conversation in a list, so a second message sees the first and its reply.
    /// </summary>
    protected List<HistoryEntry> UseRecordedHistory()
    {
        var history = new List<HistoryEntry>();
        MockHistoryService
            .Setup(h => h.CreateHistoryEntryAsync(AgentPhoneNumber, UserPhoneNumber, It.IsAny<string>(), It.IsAny<MessageType>()))
            .Callback<string, string, string, MessageType>((_, _, content, type) => history.Add(new HistoryEntry
            {
                Id = Guid.NewGuid(),
                UserPhoneNumber = UserPhoneNumber,
                Content = content,
                MessageType = type,
                Timestamp = DateTimeOffset.UtcNow,
            }))
            .Returns(Task.CompletedTask);
        MockHistoryService
            .Setup(h => h.GetHistoryAsync(AgentPhoneNumber, UserPhoneNumber))
            .ReturnsAsync(() => history.ToArray());
        return history;
    }

    /// <summary>
    /// Configures the mock to return specific calendar events when GetUserEventsAsync is called.
    /// </summary>
    protected void SetupCalendarEvents(params EventModel[] events)
    {
        MockGraphClient
            .Setup(g => g.GetUserEventsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(events);
    }

    /// <summary>
    /// Creates an EventEntry for testing.
    /// </summary>
    protected static EventModel CreateEvent(string description, DateTimeOffset start, DateTimeOffset end)
    {
        return new EventModel
        {
            Description = description,
            Start = start,
            End = end
        };
    }

    /// <summary>
    /// Configures GetUserEmailsAsync to answer by the model's search text (null for a plain listing).
    /// </summary>
    protected void SetupEmails(Func<string?, EmailModel[]> bySearch)
    {
        MockGraphClient
            .Setup(g => g.GetUserEmailsAsync(It.IsAny<MailFolder>(), It.IsAny<string?>()))
            .ReturnsAsync((MailFolder _, string? search) => bySearch(search));
    }

    /// <summary>
    /// Configures SaveEmailAsPdfAsync to succeed for any email.
    /// </summary>
    protected void SetupSave()
    {
        MockGraphClient
            .Setup(g => g.SaveEmailAsPdfAsync(It.IsAny<string>(), It.IsAny<TimeZoneInfo>(), It.IsAny<bool>()))
            .ReturnsAsync((string id, TimeZoneInfo _, bool _) => new SavedEmailModel
            {
                FileName = $"{id}.pdf",
                Saved = true,
                WebUrl = "https://example.com/onedrive/file",
                LinkedImages = 0,
            });
    }

    /// <summary>
    /// A full plain listing: 50 emails from the last day, none from the sender the tests ask about.
    /// </summary>
    protected static EmailModel[] NewestFifty() =>
        Enumerable.Range(1, 50)
            .Select(i => CreateEmail($"recent-{i}", $"sender{i}@example.com", $"Weekly update {i}", hoursAgo: i / 2.0))
            .ToArray();

    /// <summary>
    /// Creates an EmailModel for testing.
    /// </summary>
    protected static EmailModel CreateEmail(string id, string from, string subject, double hoursAgo)
    {
        return new EmailModel
        {
            Id = id,
            Subject = subject,
            From = from,
            ReceivedDateTime = DateTimeOffset.UtcNow.AddHours(-hoursAgo),
            BodyPreview = subject,
            IsRead = false,
        };
    }
}
