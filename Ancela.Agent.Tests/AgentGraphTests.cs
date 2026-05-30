using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using Ancela.Agent.Services;
using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// Integration tests verifying that calendar-related prompts trigger the correct
/// IGraphService function calls via the AI's function calling capability.
/// These hit a live model, so they are gated under the "Integration" category.
/// </summary>
[Trait("Category", "Integration")]
public class AgentGraphTests : AgentTestBase
{
    [Fact]
    public async Task GetCalendarEvents_WhenUserAsksAboutToday_QueriesTodaysDateRange()
    {
        // Arrange
        var todayStart = DateTimeOffset.UtcNow.Date;
        var todayEnd = todayStart.AddDays(1);

        SetupCalendarEvents(
            CreateEvent("Team meeting", todayStart.AddHours(10), todayStart.AddHours(11)),
            CreateEvent("Lunch", todayStart.AddHours(12), todayStart.AddHours(13)));

        // Act
        await SendMessageAsync("what's on my calendar today?");

        // Assert: the model must translate "today" into the correct date range.
        MockGraphClient.Verify(
            g => g.GetUserEventsAsync(
                It.Is<DateTimeOffset>(d => d.Date == todayStart.Date),
                It.Is<DateTimeOffset>(d => d.Date == todayEnd.Date || d.Date == todayStart.Date)),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetCalendarEvents_WhenNoEvents_StillReturnsAResponse()
    {
        // Arrange
        SetupCalendarEvents(); // Empty events

        // Act
        var response = await SendMessageAsync("what meetings do I have today?");

        // Assert
        MockGraphClient.Verify(
            g => g.GetUserEventsAsync(
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>()),
            Times.AtLeastOnce);
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
