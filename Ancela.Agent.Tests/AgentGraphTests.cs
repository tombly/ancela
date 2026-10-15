using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using Ancela.Agent.Services;
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
            MockGraphClient.Verify(g => g.SaveEmailAsPdfAsync("named", It.IsAny<TimeZoneInfo>()), Times.AtLeastOnce));

        MockGraphClient.Verify(g => g.SaveEmailAsPdfAsync(It.Is<string>(id => id != "named"), It.IsAny<TimeZoneInfo>()),
            Times.Never, "no other email may be saved in its place");
    }

    [Fact]
    public async Task SaveEmail_WhenTheNamedEmailIsNowhere_SavesNothing()
    {
        SetupEmails(search => search is null ? NewestFifty() : []);
        SetupSave();

        await SendUntilAsync("save the email from noreply@newsletter.example to OneDrive", () =>
            MockGraphClient.Verify(g => g.GetUserEmailsAsync(It.IsAny<MailFolder>(), It.IsAny<int?>(), It.IsNotNull<string>()),
                Times.AtLeastOnce, "the model has to search before concluding the email isn't there"));

        MockGraphClient.Verify(g => g.SaveEmailAsPdfAsync(It.IsAny<string>(), It.IsAny<TimeZoneInfo>()),
            Times.Never, "when the named email can't be found, nothing may be saved in its place");
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
            .Setup(g => g.GetUserEmailsAsync(It.IsAny<MailFolder>(), It.IsAny<int?>(), It.IsAny<string?>()))
            .ReturnsAsync((MailFolder _, int? _, string? search) => bySearch(search));
    }

    /// <summary>
    /// Configures SaveEmailAsPdfAsync to succeed for any email.
    /// </summary>
    protected void SetupSave()
    {
        MockGraphClient
            .Setup(g => g.SaveEmailAsPdfAsync(It.IsAny<string>(), It.IsAny<TimeZoneInfo>()))
            .ReturnsAsync((string id, TimeZoneInfo _) => new SavedEmailModel
            {
                FileName = $"{id}.pdf",
                WebUrl = "https://example.com/onedrive/file",
                BodyOnly = false,
                OmittedAttachments = [],
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
