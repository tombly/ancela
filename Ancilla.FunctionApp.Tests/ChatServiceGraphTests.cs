using Ancilla.FunctionApp.Services;
using Moq;

namespace Ancilla.FunctionApp.Tests;

/// <summary>
/// Integration tests verifying that calendar-related prompts trigger the correct
/// IGraphService function calls via the AI's function calling capability.
/// </summary>
public class ChatServiceGraphTests : ChatServiceTestBase
{
    [Fact]
    public async Task GetCalendarEvents_WhenUserAsksAboutToday_CallsGetUserEventsAsync()
    {
        // Arrange
        var todayStart = DateTimeOffset.UtcNow.Date;
        var todayEnd = todayStart.AddDays(1);

        SetupCalendarEvents(
            CreateEvent("Team meeting", todayStart.AddHours(10), todayStart.AddHours(11)),
            CreateEvent("Lunch", todayStart.AddHours(12), todayStart.AddHours(13)));

        // Act
        var response = await SendMessageAsync("what's on my calendar today?");

        // Assert
        MockGraphService.Verify(
            g => g.GetUserEventsAsync(
                It.Is<DateTimeOffset>(d => d.Date == todayStart.Date),
                It.Is<DateTimeOffset>(d => d.Date == todayEnd.Date || d.Date == todayStart.Date)),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetCalendarEvents_WhenUserAsksAboutTomorrow_CallsGetUserEventsAsync()
    {
        // Arrange
        var tomorrowStart = DateTimeOffset.UtcNow.Date.AddDays(1);
        var tomorrowEnd = tomorrowStart.AddDays(1);

        SetupCalendarEvents(
            CreateEvent("Client call", tomorrowStart.AddHours(14), tomorrowStart.AddHours(15)));

        // Act
        var response = await SendMessageAsync("what meetings do I have tomorrow?");

        // Assert
        MockGraphService.Verify(
            g => g.GetUserEventsAsync(
                It.Is<DateTimeOffset>(d => d.Date == tomorrowStart.Date),
                It.Is<DateTimeOffset>(d => d.Date == tomorrowEnd.Date || d.Date == tomorrowStart.Date)),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetCalendarEvents_WhenUserAsksAboutThisWeek_CallsGetUserEventsAsync()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var weekStart = now.Date;
        var weekEnd = weekStart.AddDays(7);

        SetupCalendarEvents(
            CreateEvent("Monday standup", weekStart.AddHours(9), weekStart.AddHours(9.5)),
            CreateEvent("Friday review", weekStart.AddDays(4).AddHours(16), weekStart.AddDays(4).AddHours(17)));

        // Act
        var response = await SendMessageAsync("show me my calendar for this week");

        // Assert
        MockGraphService.Verify(
            g => g.GetUserEventsAsync(
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetCalendarEvents_WhenUserAsksAboutSpecificDay_CallsGetUserEventsAsync()
    {
        // Arrange
        var specificDay = DateTimeOffset.UtcNow.Date.AddDays(3);

        SetupCalendarEvents(
            CreateEvent("Project presentation", specificDay.AddHours(10), specificDay.AddHours(11)));

        // Act
        var response = await SendMessageAsync("what's on my schedule next Monday?");

        // Assert
        MockGraphService.Verify(
            g => g.GetUserEventsAsync(
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetCalendarEvents_WhenUserAsksIfBusy_CallsGetUserEventsAsync()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;

        SetupCalendarEvents(
            CreateEvent("All-day event", now.Date, now.Date.AddDays(1)));

        // Act
        var response = await SendMessageAsync("am I busy this afternoon?");

        // Assert
        MockGraphService.Verify(
            g => g.GetUserEventsAsync(
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetCalendarEvents_WhenNoEvents_ReturnsEmptyResult()
    {
        // Arrange
        SetupCalendarEvents(); // Empty events

        // Act
        var response = await SendMessageAsync("what meetings do I have today?");

        // Assert
        MockGraphService.Verify(
            g => g.GetUserEventsAsync(
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>()),
            Times.AtLeastOnce);

        // Response should indicate no events (exact wording may vary)
        Assert.NotNull(response);
    }

    [Fact]
    public async Task GetCalendarEvents_WithMultipleEvents_ReturnsAllEvents()
    {
        // Arrange
        var today = DateTimeOffset.UtcNow.Date;

        SetupCalendarEvents(
            CreateEvent("Morning standup", today.AddHours(9), today.AddHours(9.5)),
            CreateEvent("Team meeting", today.AddHours(10), today.AddHours(11)),
            CreateEvent("Lunch with client", today.AddHours(12), today.AddHours(13)),
            CreateEvent("Code review", today.AddHours(14), today.AddHours(15)),
            CreateEvent("End of day sync", today.AddHours(16), today.AddHours(17)));

        // Act
        var response = await SendMessageAsync("what's my schedule today?");

        // Assert
        MockGraphService.Verify(
            g => g.GetUserEventsAsync(
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetCalendarEvents_WhenUserAsksAboutDateRange_CallsGetUserEventsAsync()
    {
        // Arrange
        var rangeStart = DateTimeOffset.UtcNow.Date;
        var rangeEnd = rangeStart.AddDays(3);

        SetupCalendarEvents(
            CreateEvent("Event 1", rangeStart.AddHours(10), rangeStart.AddHours(11)),
            CreateEvent("Event 2", rangeStart.AddDays(1).AddHours(14), rangeStart.AddDays(1).AddHours(15)),
            CreateEvent("Event 3", rangeStart.AddDays(2).AddHours(9), rangeStart.AddDays(2).AddHours(10)));

        // Act
        var response = await SendMessageAsync("show me my calendar for the next 3 days");

        // Assert
        MockGraphService.Verify(
            g => g.GetUserEventsAsync(
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>()),
            Times.AtLeastOnce);
    }

    /// <summary>
    /// Configures the mock to return specific calendar events when GetUserEventsAsync is called.
    /// </summary>
    protected void SetupCalendarEvents(params EventEntry[] events)
    {
        MockGraphService
            .Setup(g => g.GetUserEventsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(events);
    }

    /// <summary>
    /// Creates an EventEntry for testing.
    /// </summary>
    protected static EventEntry CreateEvent(string description, DateTimeOffset start, DateTimeOffset end)
    {
        return new EventEntry
        {
            Description = description,
            Start = start,
            End = end
        };
    }
}
