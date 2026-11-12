using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using FluentAssertions;

namespace Ancela.Agent.Tests;

/// <summary>
/// The parts of iCloud's calendar (CalDavCalendarClient) that need no server: which occurrences of an
/// event fall in a range, on the user's clock, and a new event as iCalendar text. The shapes are
/// iCloud's (probed 2026-11-11): a series with its moved occurrences in one resource, a VTIMEZONE,
/// all-day events as dates. ICloudCalendarContactsLiveTests covers the calls against the real account.
/// </summary>
public class CalDavCalendarTests
{
    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
    private static readonly DateTimeOffset RangeStart = new(2026, 11, 16, 0, 0, 0, TimeSpan.FromHours(-8));
    private static readonly DateTimeOffset RangeEnd = new(2026, 11, 30, 0, 0, 0, TimeSpan.FromHours(-8));

    private const string PacificZone = """
        BEGIN:VTIMEZONE
        TZID:America/Los_Angeles
        BEGIN:DAYLIGHT
        TZOFFSETFROM:-0800
        RRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=2SU
        DTSTART:20070311T020000
        TZNAME:PDT
        TZOFFSETTO:-0700
        END:DAYLIGHT
        BEGIN:STANDARD
        TZOFFSETFROM:-0700
        RRULE:FREQ=YEARLY;BYMONTH=11;BYDAY=1SU
        DTSTART:20071104T020000
        TZNAME:PST
        TZOFFSETTO:-0800
        END:STANDARD
        END:VTIMEZONE
        """;

    /// <summary>Every other Thursday at 10 from Oct 1: Oct 1, 15, 29, Nov 12, 26, Dec 10, …; plus the given overrides.</summary>
    private static string Fortnightly(string overrides = "") => NoBlankLines($"""
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Apple Inc.//iPhone OS 18.0//EN
        {PacificZone}
        BEGIN:VEVENT
        UID:fortnightly-1
        DTSTAMP:20261001T000000Z
        DTSTART;TZID=America/Los_Angeles:20261001T100000
        DTEND;TZID=America/Los_Angeles:20261001T110000
        RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=TH
        SUMMARY:Planning
        END:VEVENT
        {overrides}
        END:VCALENDAR
        """);

    // iCloud sends no blank lines; the templates leave one where an optional part is empty.
    private static string NoBlankLines(string ics) => string.Join("\n", ics.Split('\n').Where(l => l.Trim().Length > 0));

    private static string Override(string recurrenceId, string start, string end, string extra = "") => NoBlankLines($"""
        BEGIN:VEVENT
        UID:fortnightly-1
        DTSTAMP:20261001T000000Z
        RECURRENCE-ID;TZID=America/Los_Angeles:{recurrenceId}
        DTSTART;TZID=America/Los_Angeles:{start}
        DTEND;TZID=America/Los_Angeles:{end}
        SUMMARY:Planning (moved)
        {extra}
        END:VEVENT
        """) + "\n";

    private static string Event(string body) => $"""
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Apple Inc.//iPhone OS 18.0//EN
        BEGIN:VEVENT
        UID:one-1
        DTSTAMP:20261001T000000Z
        {body}
        END:VEVENT
        END:VCALENDAR
        """;

    [Fact]
    public void ASeries_GivesEachOccurrenceInTheRange_OnTheUsersClock()
    {
        var events = CalDavCalendarClient.Occurrences(Fortnightly(), RangeStart, RangeEnd, Pacific).ToList();

        events.Should().ContainSingle();
        events[0].Description.Should().Be("Planning");
        events[0].Start.Should().Be(new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.FromHours(-8)));
        events[0].Start.Offset.Should().Be(TimeSpan.FromHours(-8));
        events[0].End.Should().Be(events[0].Start.AddHours(1));
        events[0].IsAllDay.Should().BeFalse();
    }

    [Fact]
    public void AnOccurrenceMovedIntoTheRange_Counts_FromBeforeItOrAfter()
    {
        // iCloud's own expand left out an occurrence moved into the range from before it (2026-11-11).
        var ics = Fortnightly(
            Override("20261112T100000", "20261119T090000", "20261119T100000") +
            Override("20261210T100000", "20261120T140000", "20261120T150000"));

        var starts = CalDavCalendarClient.Occurrences(ics, RangeStart, RangeEnd, Pacific).Select(e => e.Start).ToList();

        starts.Should().Equal(
            new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.FromHours(-8)),
            new DateTimeOffset(2026, 11, 20, 14, 0, 0, TimeSpan.FromHours(-8)),
            new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.FromHours(-8)));
    }

    [Fact]
    public void ACancelledOccurrence_IsLeftOut()
    {
        var ics = Fortnightly(Override("20261126T100000", "20261126T100000", "20261126T110000", "STATUS:CANCELLED"));

        CalDavCalendarClient.Occurrences(ics, RangeStart, RangeEnd, Pacific).Should().BeEmpty();
    }

    [Fact]
    public void AnAllDayEvent_RunsMidnightToMidnight_InTheUsersZone()
    {
        var ics = Event("DTSTART;VALUE=DATE:20261120\nDTEND;VALUE=DATE:20261121\nSUMMARY:Day off");

        var e = CalDavCalendarClient.Occurrences(ics, RangeStart, RangeEnd, Pacific).Single();

        e.IsAllDay.Should().BeTrue();
        e.Start.Should().Be(new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.FromHours(-8)));
        e.End.Should().Be(new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.FromHours(-8)));
    }

    [Fact]
    public void AnEventThatBeganBeforeTheRange_AndRunsIntoIt_Counts_ButOneThatEnded_DoesNot()
    {
        var running = Event("DTSTART:20261114T170000Z\nDTEND:20261117T170000Z\nSUMMARY:Conference");
        var over = Event("DTSTART:20261114T170000Z\nDTEND:20261115T170000Z\nSUMMARY:Earlier");

        CalDavCalendarClient.Occurrences(running, RangeStart, RangeEnd, Pacific).Should().ContainSingle()
            .Which.Start.Should().Be(new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.FromHours(-8)));
        CalDavCalendarClient.Occurrences(over, RangeStart, RangeEnd, Pacific).Should().BeEmpty();
    }

    [Fact]
    public void AnEventAfterTheRange_IsLeftOut() =>
        CalDavCalendarClient.Occurrences(Event("DTSTART:20261201T170000Z\nDTEND:20261201T180000Z\nSUMMARY:Later"), RangeStart, RangeEnd, Pacific)
            .Should().BeEmpty();

    [Fact]
    public void ANewEvent_IsReadOnTheUsersClock_AndWrittenInUtc()
    {
        var (ics, created) = CalDavCalendarClient.NewEvent("uid-1", "Dentist; bring card, insurance", "2026-11-20T15:00:00", "2026-11-20T16:00:00",
            Pacific, "Main St", "Line one\nLine two", isAllDay: false, new DateTime(2026, 11, 11, 0, 0, 0, DateTimeKind.Utc));

        ics.Should().Contain("DTSTART:20261120T230000Z").And.Contain("DTEND:20261121T000000Z").And.Contain("UID:uid-1");
        created.Start.Should().Be(new DateTimeOffset(2026, 11, 20, 15, 0, 0, TimeSpan.FromHours(-8)));
        var read = CalDavCalendarClient.Occurrences(ics, RangeStart, RangeEnd, Pacific).Single();
        read.Description.Should().Be("Dentist; bring card, insurance", because: "the text is escaped, not cut at a ; or ,");
        read.Start.Should().Be(created.Start);
        read.End.Should().Be(created.End);
    }

    [Fact]
    public void ATimeWithAnOffset_IsThatInstant() =>
        CalDavCalendarClient.NewEvent("uid-2", "Call", "2026-11-20T18:00:00-05:00", "2026-11-20T19:00:00-05:00", Pacific, null, null, false, DateTime.UtcNow)
            .Event.Start.Should().Be(new DateTimeOffset(2026, 11, 20, 15, 0, 0, TimeSpan.FromHours(-8)));

    [Fact]
    public void ANewAllDayEvent_IsDatesOnly_EndingTheDayAfter()
    {
        var (ics, created) = CalDavCalendarClient.NewEvent("uid-3", "Day off", "2026-11-20T00:00:00", "2026-11-20T00:00:00", Pacific, null, null, isAllDay: true, DateTime.UtcNow);

        ics.Should().Contain("DTSTART;VALUE=DATE:20261120").And.Contain("DTEND;VALUE=DATE:20261121");
        created.IsAllDay.Should().BeTrue();
        CalDavCalendarClient.Occurrences(ics, RangeStart, RangeEnd, Pacific).Single().End
            .Should().Be(new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.FromHours(-8)));
    }

    private static CalDavCalendarClient.EventCalendar Calendar(string name, bool writable, bool shared) =>
        new(new Uri($"https://p01-caldav.icloud.com/1/calendars/{name}/"), writable, shared);

    [Fact]
    public void ANewEvent_GoesOnTheCalendarSharedWithTheOwner_ElseTheirOwn()
    {
        var own = Calendar("own", writable: true, shared: false);
        var shared = Calendar("shared", writable: true, shared: true);

        CalDavCalendarClient.CalendarForNewEvents([own, shared]).Should().Be(shared, because: "it is the one the owner uses");
        CalDavCalendarClient.CalendarForNewEvents([own]).Should().Be(own);
        CalDavCalendarClient.CalendarForNewEvents([own, Calendar("read-only", writable: false, shared: true)]).Should().Be(own);
    }

    [Fact]
    public void ANewEvent_WithTwoSharedCalendars_OrNoneToWrite_IsRefused()
    {
        FluentActions.Invoking(() => CalDavCalendarClient.CalendarForNewEvents([Calendar("a", true, true), Calendar("b", true, true)]))
            .Should().Throw<InvalidOperationException>().WithMessage("*doesn't know which*");
        FluentActions.Invoking(() => CalDavCalendarClient.CalendarForNewEvents([Calendar("a", false, true)]))
            .Should().Throw<InvalidOperationException>().WithMessage("*no iCloud calendar*");
    }

    [Theory]
    [InlineData("2026-11-20T16:00:00", "2026-11-20T15:00:00", "*ends before it starts*")]
    [InlineData("next Tuesday", "2026-11-20T15:00:00", "*isn't a date and time*")]
    public void ANewEvent_WithTimesThatDontWork_IsRefused(string start, string end, string message) =>
        FluentActions.Invoking(() => CalDavCalendarClient.NewEvent("uid-4", "x", start, end, Pacific, null, null, false, DateTime.UtcNow))
            .Should().Throw<InvalidOperationException>().WithMessage(message);
}
