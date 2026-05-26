using Ancela.Agent.SemanticKernel.Plugins.ScheduledTaskPlugin;
using FluentAssertions;

namespace Ancela.Agent.Tests;

/// <summary>
/// Unit tests for the timezone/DST math behind recurring scheduled tasks.
/// </summary>
public class ScheduleCalculatorTests
{
    private const string LosAngeles = "America/Los_Angeles";
    private static readonly DayOfWeek[] EveryDay =
        [DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
         DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday];

    private static readonly TimeZoneInfo LaTz = TimeZoneInfo.FindSystemTimeZoneById(LosAngeles);

    [Fact]
    public void NextRun_WhenSlotIsLaterToday_ReturnsToday()
    {
        // Local 2026-06-01 06:00 PDT (UTC-7) == 13:00Z; target 07:00 local.
        var utcNow = new DateTimeOffset(2026, 6, 1, 13, 0, 0, TimeSpan.Zero);

        var next = ScheduleCalculator.NextRun("07:00", EveryDay, LosAngeles, utcNow);

        next.ToUniversalTime().Should().Be(new DateTimeOffset(2026, 6, 1, 14, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void NextRun_WhenSlotAlreadyPassedToday_ReturnsTomorrow()
    {
        // Local 2026-06-01 08:00 PDT == 15:00Z; target 07:00 already passed today.
        var utcNow = new DateTimeOffset(2026, 6, 1, 15, 0, 0, TimeSpan.Zero);

        var next = ScheduleCalculator.NextRun("07:00", EveryDay, LosAngeles, utcNow);

        next.ToUniversalTime().Should().Be(new DateTimeOffset(2026, 6, 2, 14, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void NextRun_WithWeekdayFilter_LandsOnAllowedDay()
    {
        // Local 2026-06-03 12:00 PDT == 19:00Z; only Mondays allowed.
        var utcNow = new DateTimeOffset(2026, 6, 3, 19, 0, 0, TimeSpan.Zero);

        var next = ScheduleCalculator.NextRun("09:00", [DayOfWeek.Monday], LosAngeles, utcNow);

        var local = TimeZoneInfo.ConvertTime(next, LaTz);
        local.DayOfWeek.Should().Be(DayOfWeek.Monday);
        local.TimeOfDay.Should().Be(new TimeSpan(9, 0, 0));
        next.Should().BeAfter(utcNow);
    }

    [Fact]
    public void NextRun_AcrossSpringForwardGap_NudgesPastNonexistentTime()
    {
        // US DST begins 2026-03-08: 02:00 jumps to 03:00, so 02:30 local does not exist.
        // Local 2026-03-08 00:00 PST (UTC-8) == 08:00Z; target 02:30 -> nudged to 03:30 PDT.
        var utcNow = new DateTimeOffset(2026, 3, 8, 8, 0, 0, TimeSpan.Zero);

        var next = ScheduleCalculator.NextRun("02:30", EveryDay, LosAngeles, utcNow);

        // 03:30 PDT (UTC-7) == 10:30Z
        next.ToUniversalTime().Should().Be(new DateTimeOffset(2026, 3, 8, 10, 30, 0, TimeSpan.Zero));
        LaTz.IsInvalidTime(TimeZoneInfo.ConvertTime(next, LaTz).DateTime).Should().BeFalse();
    }

    [Theory]
    [InlineData("07:00", true)]
    [InlineData("7:00", true)]
    [InlineData("23:59", true)]
    [InlineData("25:00", false)]
    [InlineData("7am", false)]
    [InlineData("", false)]
    public void TryParseTimeOfDay_ValidatesFormat(string input, bool expected)
    {
        ScheduleCalculator.TryParseTimeOfDay(input, out _).Should().Be(expected);
    }
}
