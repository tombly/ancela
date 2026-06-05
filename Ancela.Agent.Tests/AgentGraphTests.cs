using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using Ancela.Agent.Services;
using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// Integration tests verifying that calendar-related prompts trigger the correct
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
        var userTz = TimeZoneInfo.FindSystemTimeZoneById(TestUser.TimeZone);
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
}
