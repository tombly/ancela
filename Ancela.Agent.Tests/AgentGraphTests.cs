using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using Ancela.Agent.SemanticKernel.Plugins.RemarkablePlugin;
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
                ReceivedDateTime = setup.ReceivedDateTime, Body = steps, Truncated = false, HasAttachments = false,
            });

        await SendUntilAsync("what's the last step in the account setup email from my bank?", reply =>
        {
            MockGraphClient.Verify(g => g.GetEmailAsync("setup"), Times.AtLeastOnce);
            reply.Should().ContainEquivalentOf("two-factor", because: $"step 7 is only in the full email; the reply was: {reply}");
        });
    }

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
