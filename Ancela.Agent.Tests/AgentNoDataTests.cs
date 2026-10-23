using System.ComponentModel;
using System.Text.RegularExpressions;
using Ancela.Agent.SemanticKernel.Plugins.GoogleHealthPlugin.Models;
using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using Ancela.Agent.Services;
using FluentAssertions;
using Microsoft.SemanticKernel;
using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// What the agent says, end to end: a lookup that worked and found nothing is reported as
/// nothing, and one that failed as a failure, never the other way round. On 2026-10-20 a sleep
/// lookup that worked, for a night with nothing recorded, was reported as "Google Health still
/// appears disconnected". Live model, so retried; the calendar stands in for every lookup.
/// </summary>
[Trait("Category", "Integration")]
public class AgentNoDataTests : AgentTestBase
{
    private static readonly Regex SoundsLikeAFailure = new(
        @"couldn['’]?t|could not|unable|error|trouble|problem|issue|fail|connect|access|reach", RegexOptions.IgnoreCase);

    private static readonly Regex SoundsLikeNothingScheduled = new(
        @"\bno (events|meetings|appointments)|nothing (is )?(on|scheduled|planned)|\bfree\b|\bclear\b|\bempty\b|don['’]?t have anything", RegexOptions.IgnoreCase);

    [Fact]
    public async Task EmptyCalendar_IsReportedAsNothing_NotAsAFailure()
    {
        MockGraphClient.Setup(g => g.GetUserEventsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>()))
            .ReturnsAsync([]);

        await SendEveryTimeAsync("what's on my calendar today?", reply =>
        {
            MockGraphClient.Verify(g => g.GetUserEventsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>()), Times.AtLeastOnce);
            SoundsLikeNothingScheduled.IsMatch(reply).Should().BeTrue($"the calendar was read and had nothing, but the reply was: {reply}");
            SoundsLikeAFailure.IsMatch(reply).Should().BeFalse($"nothing failed, but the reply was: {reply}");
        });
    }

    [Fact]
    public async Task EmptyCalendar_AfterAnEarlierOutage_IsStillReportedAsNothing()
    {
        // The 2026-10-20 shape: an earlier self-check in history said the service was down, then
        // a lookup worked and found nothing, and the reply blamed the old outage.
        var earlier = DateTimeOffset.UtcNow.AddMinutes(-30);
        MockHistoryService.Setup(h => h.GetHistoryAsync(AgentPhoneNumber, UserPhoneNumber)).ReturnsAsync(
        [
            new HistoryEntry { Id = Guid.NewGuid(), UserPhoneNumber = UserPhoneNumber, MessageType = MessageType.User, Timestamp = earlier, Content = "How are you feeling?" },
            new HistoryEntry { Id = Guid.NewGuid(), UserPhoneNumber = UserPhoneNumber, MessageType = MessageType.Agent, Timestamp = earlier,
                Content = "One issue: Microsoft Graph is failing, so your calendar is disconnected and needs attention. Everything else looks healthy." },
        ]);
        MockGraphClient.Setup(g => g.GetUserEventsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>()))
            .ReturnsAsync([]);

        await SendEveryTimeAsync("what's on my calendar today?", reply =>
        {
            MockGraphClient.Verify(g => g.GetUserEventsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>()), Times.AtLeastOnce);
            SoundsLikeAFailure.IsMatch(reply).Should().BeFalse($"the calendar was read and had nothing, but the reply was: {reply}");
        });
    }

    // "Couldn't find" is how an absence is said for a lookup by name, so it isn't a failure here.
    private static readonly Regex SoundsLikeAnOutage = new(
        @"error|trouble|problem|connect|access|reach|fail|\bdown\b|unavailable", RegexOptions.IgnoreCase);

    [Fact]
    public async Task ContactNotFound_AfterAnEarlierOutage_IsReportedAsNotFound()
    {
        // get_contact_by_name returns null for no match, which reaches the model as "" unless the
        // filter says what it means: the same shape as the sleep summary that was misreported.
        var earlier = DateTimeOffset.UtcNow.AddMinutes(-30);
        MockHistoryService.Setup(h => h.GetHistoryAsync(AgentPhoneNumber, UserPhoneNumber)).ReturnsAsync(
        [
            new HistoryEntry { Id = Guid.NewGuid(), UserPhoneNumber = UserPhoneNumber, MessageType = MessageType.User, Timestamp = earlier, Content = "How are you feeling?" },
            new HistoryEntry { Id = Guid.NewGuid(), UserPhoneNumber = UserPhoneNumber, MessageType = MessageType.Agent, Timestamp = earlier,
                Content = "One issue: Microsoft Graph is failing, so your contacts are disconnected and need attention. Everything else looks healthy." },
        ]);
        MockGraphClient.Setup(g => g.GetUserContactByNameAsync(It.IsAny<string>())).ReturnsAsync((ContactModel?)null);

        await SendEveryTimeAsync("what's Priya Raman's phone number?", reply =>
        {
            MockGraphClient.Verify(g => g.GetUserContactByNameAsync(It.IsAny<string>()), Times.AtLeastOnce);
            SoundsLikeAnOutage.IsMatch(reply).Should().BeFalse($"the lookup worked and found no such contact, but the reply was: {reply}");
        });
    }

    [Fact]
    public async Task NightWithNoSleepRecorded_AfterAnEarlierDisconnect_IsNotReportedAsDisconnected()
    {
        // The 2026-10-20 incident as it happened: the self-check had said Google Health was
        // disconnected, the token was then renewed, and the sleep lookup worked for a night the
        // watch hadn't synced. The reply was "Google Health still appears disconnected". Here the
        // old record of nulls was misreported in 2 of 18 replies ("I couldn't retrieve last night's
        // sleep data"), and null in none of 24: a check, not a reliable guard. The guard is
        // GoogleHealthClientTests.Sleep_NightWithNothingRecorded_IsNull.
        var earlier = DateTimeOffset.UtcNow.AddMinutes(-45);
        MockHistoryService.Setup(h => h.GetHistoryAsync(AgentPhoneNumber, UserPhoneNumber)).ReturnsAsync(
        [
            new HistoryEntry { Id = Guid.NewGuid(), UserPhoneNumber = UserPhoneNumber, MessageType = MessageType.User, Timestamp = earlier, Content = "How are you feeling?" },
            new HistoryEntry { Id = Guid.NewGuid(), UserPhoneNumber = UserPhoneNumber, MessageType = MessageType.Agent, Timestamp = earlier,
                Content = "Two issues: YNAB timed out, and Google Health is disconnected and needs reauth. Everything else looks healthy, and anomaly scan is all-clear for the last 24h." },
        ]);
        // Saved minutes before, and its "I'm afraid I can't" pulls a reply towards a refusal.
        TestUser.Instructions = [new UserInstruction { Id = Guid.NewGuid(), Text = "Take on a HAL 9000-like personality when talking to me.", CreatedAt = earlier }];
        var sleep = new SleepStandIn();
        ExtraPlugins.Add(sleep);

        await SendEveryTimeAsync("How did I sleep last night?", reply =>
        {
            sleep.Calls.Should().BeGreaterThan(0);
            SoundsLikeAnOutage.IsMatch(reply).Should().BeFalse($"the lookup worked and found no sleep, but the reply was: {reply}");
        });
    }

    /// <summary>What get_sleep_summary returns for a night with no sleep recorded.</summary>
    private sealed class SleepStandIn
    {
        public int Calls;

        [KernelFunction("get_sleep_summary")]
        [Description("Gets sleep duration, efficiency, and stage breakdown (deep/light/rem/awake) for a date. Defaults to today.")]
        public SleepSummaryModel? GetSleepSummary([Description("Date in YYYY-MM-DD format, or 'today'. Defaults to today.")] string? date = null)
        {
            Interlocked.Increment(ref Calls);
            return null;
        }
    }

    [Fact]
    public async Task FailedCalendarRead_IsReportedAsAFailure_NotAsNothing()
    {
        MockGraphClient.Setup(g => g.GetUserEventsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>()))
            .ThrowsAsync(new HttpRequestException("Graph calendarView request failed with 503: Service Unavailable"));

        await SendEveryTimeAsync("what's on my calendar today?", reply =>
        {
            SoundsLikeAFailure.IsMatch(reply).Should().BeTrue($"the read failed, but the reply was: {reply}");
            SoundsLikeNothingScheduled.IsMatch(reply).Should().BeFalse($"the calendar was never read, but the reply was: {reply}");
        });
    }
}
