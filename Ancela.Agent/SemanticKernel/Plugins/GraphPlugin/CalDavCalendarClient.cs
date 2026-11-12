using System.Globalization;
using System.Xml.Linq;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Calendar = Ical.Net.Calendar;

namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

/// <summary>
/// The owner's iCloud calendars over CalDAV (<see cref="ICloudDav"/>): the same tools and results as
/// Graph's (<see cref="GraphClient"/>). Every calendar of events is read, a calendar someone shared
/// with the owner too, as Graph's calendar list has them; Reminders' to-do list isn't one. Repeating
/// events are expanded here (Ical.Net), not by iCloud: on 2026-11-11 its expand left out an
/// occurrence that had been moved into the range, which the owner's calendar shows.
/// </summary>
public sealed class CalDavCalendarClient : ICalendarClient, IDisposable
{
    private readonly ICloudDav _dav = new(new Uri("https://caldav.icloud.com/"), ICloudDav.CalDav + "calendar-home-set", "calendar");

    public record EventCalendar(Uri Href, bool Writable, bool SharedWithOwner);

    public async Task<EventModel[]> GetUserEventsAsync(DateTimeOffset start, DateTimeOffset end, TimeZoneInfo timeZone)
    {
        var events = new List<EventModel>();
        foreach (var calendar in await CalendarsAsync())
        {
            // iCloud returns each event with an occurrence in the range, a repeating one whole.
            var report = await _dav.SendAsync("REPORT", calendar.Href, 1, Query(start, end));
            foreach (var (_, response) in ICloudDav.Responses(report, calendar.Href))
            {
                var ics = response.Descendants(ICloudDav.CalDav + "calendar-data").FirstOrDefault()?.Value
                    ?? throw new InvalidOperationException("iCloud listed an event without its details.");
                events.AddRange(Occurrences(ics, start, end, timeZone));
            }
        }
        return [.. events.OrderBy(e => e.Start)];
    }

    public async Task<EventModel> CreateEventAsync(string subject, string start, string end, TimeZoneInfo timeZone, string? location = null, string? body = null, bool isAllDay = false)
    {
        var calendar = CalendarForNewEvents(await CalendarsAsync());
        var uid = Guid.NewGuid().ToString().ToUpperInvariant();
        var (ics, created) = NewEvent(uid, subject, start, end, timeZone, location, body, isAllDay, DateTime.UtcNow);
        await _dav.PutNewAsync(new Uri(calendar.Href, $"{uid}.ics"), "text/calendar", ics);
        return created;
    }

    /// <summary>For the health probe: how many calendars of events the account has.</summary>
    public async Task<int> CheckAsync() => (await CalendarsAsync()).Count;

    /// <summary>
    /// Where a new event goes: the calendar shared with the owner, the one they use (owner,
    /// 2026-11-11: their own iCloud calendar is empty), else their own. Everyone it is shared with
    /// sees what is added. Only a calendar the owner may write to; with two shared ones, it doesn't
    /// guess which.
    /// </summary>
    public static EventCalendar CalendarForNewEvents(IReadOnlyList<EventCalendar> calendars)
    {
        var writable = calendars.Where(c => c.Writable).ToList();
        var shared = writable.Where(c => c.SharedWithOwner).ToList();
        if (shared.Count > 1)
            throw new InvalidOperationException("More than one calendar is shared with the owner, and Ancela doesn't know which one new events go on. Nothing was added.");
        return shared.SingleOrDefault() ?? writable.FirstOrDefault()
            ?? throw new InvalidOperationException("The owner has no iCloud calendar Ancela can add an event to. Nothing was added.");
    }

    /// <summary>
    /// The occurrences of one iCloud event that overlap the range, on the user's clock. An event can
    /// be a series with moved or changed occurrences in it; a cancelled one is left out.
    /// </summary>
    public static IEnumerable<EventModel> Occurrences(string ics, DateTimeOffset start, DateTimeOffset end, TimeZoneInfo timeZone)
    {
        var calendar = Calendar.Load(ics) ?? throw new InvalidOperationException("iCloud returned an event that couldn't be read.");
        // From each series' first occurrence, so one that began before the range and runs into it counts,
        // as in Graph's calendar view.
        foreach (var occurrence in calendar.GetOccurrences())
        {
            var first = OnClock(occurrence.Period.StartTime, timeZone);
            if (first >= end)
                yield break;
            var last = OnClock(occurrence.Period.EffectiveEndTime ?? occurrence.Period.StartTime, timeZone);
            if (last <= start && first < start)
                continue;
            if (occurrence.Source is not CalendarEvent e || string.Equals(e.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
                continue;
            yield return new EventModel
            {
                Description = e.Summary ?? string.Empty,
                Start = first,
                End = last,
                IsAllDay = !occurrence.Period.StartTime.HasTime,
            };
        }
    }

    /// <summary>
    /// The new event as iCalendar text. The times come from the model on the user's clock, as the
    /// tool describes, so they are read in <paramref name="timeZone"/> and written in UTC; an all-day
    /// event is dates only, ending the day after its last day.
    /// </summary>
    public static (string Ics, EventModel Event) NewEvent(string uid, string subject, string start, string end, TimeZoneInfo timeZone,
        string? location, string? body, bool isAllDay, DateTime stampUtc)
    {
        var first = Parse(start, timeZone);
        var last = Parse(end, timeZone);
        var e = new CalendarEvent
        {
            Uid = uid,
            Summary = subject,
            Location = string.IsNullOrWhiteSpace(location) ? null : location,
            Description = string.IsNullOrWhiteSpace(body) ? null : body,
            DtStamp = new CalDateTime(stampUtc, "UTC"),
        };
        if (isAllDay)
        {
            var firstDay = DateOnly.FromDateTime(first.DateTime);
            var lastDay = DateOnly.FromDateTime(last.DateTime);
            if (lastDay <= firstDay)
                lastDay = firstDay.AddDays(1);
            e.DtStart = new CalDateTime(firstDay);
            e.DtEnd = new CalDateTime(lastDay);
            first = Midnight(firstDay, timeZone);
            last = Midnight(lastDay, timeZone);
        }
        else
        {
            if (last < first)
                throw new InvalidOperationException("The event ends before it starts.");
            e.DtStart = new CalDateTime(first.UtcDateTime, "UTC");
            e.DtEnd = new CalDateTime(last.UtcDateTime, "UTC");
        }

        var calendar = new Calendar();
        calendar.Events.Add(e);
        var ics = new CalendarSerializer().SerializeToString(calendar)
            ?? throw new InvalidOperationException("The event couldn't be written.");
        return (ics, new EventModel { Description = subject, Start = first, End = last, IsAllDay = isAllDay });
    }

    /// <summary>The account's calendars of events, from the home's listing.</summary>
    private async Task<List<EventCalendar>> CalendarsAsync()
    {
        var home = await _dav.HomeAsync();
        var listing = await _dav.SendAsync("PROPFIND", home, 1, ICloudDav.Propfind(
            ICloudDav.Dav + "resourcetype", ICloudDav.CalDav + "supported-calendar-component-set", ICloudDav.Dav + "current-user-privilege-set"));
        return [.. ICloudDav.Responses(listing, home)
            // Selected by type: iCloud's scheduling inbox and outbox take events too, and a REPORT on one is refused.
            .Where(r => r.Response.Descendants(ICloudDav.Dav + "resourcetype").Elements(ICloudDav.CalDav + "calendar").Any())
            .Where(r => r.Response.Descendants(ICloudDav.CalDav + "supported-calendar-component-set").FirstOrDefault() is not { } set
                || set.Elements(ICloudDav.CalDav + "comp").Any(c => (string?)c.Attribute("name") == "VEVENT"))
            .Select(r => new EventCalendar(
                r.Href,
                r.Response.Descendants(ICloudDav.Dav + "privilege").Elements().Any(p => p.Name == ICloudDav.Dav + "write" || p.Name == ICloudDav.Dav + "all"),
                // A calendar someone shared with the owner is marked "shared"; one the owner shares, "shared-owner".
                r.Response.Descendants(ICloudDav.Dav + "resourcetype").Elements().Any(t => t.Name.LocalName == "shared")))];
    }

    private static XElement Query(DateTimeOffset start, DateTimeOffset end)
    {
        XNamespace d = ICloudDav.Dav, c = ICloudDav.CalDav;
        static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        return new XElement(c + "calendar-query", new XAttribute(XNamespace.Xmlns + "d", d.NamespaceName), new XAttribute(XNamespace.Xmlns + "c", c.NamespaceName),
            new XElement(d + "prop", new XElement(c + "calendar-data")),
            new XElement(c + "filter",
                new XElement(c + "comp-filter", new XAttribute("name", "VCALENDAR"),
                    new XElement(c + "comp-filter", new XAttribute("name", "VEVENT"),
                        new XElement(c + "time-range", new XAttribute("start", Utc(start)), new XAttribute("end", Utc(end)))))));
    }

    /// <summary>An iCalendar time on the user's clock. A floating time, or an all-day date, is on it already.</summary>
    private static DateTimeOffset OnClock(CalDateTime time, TimeZoneInfo timeZone) =>
        time.IsFloating
            ? new DateTimeOffset(DateTime.SpecifyKind(time.Value, DateTimeKind.Unspecified), timeZone.GetUtcOffset(DateTime.SpecifyKind(time.Value, DateTimeKind.Unspecified)))
            : TimeZoneInfo.ConvertTime(new DateTimeOffset(time.AsUtc, TimeSpan.Zero), timeZone);

    private static DateTimeOffset Midnight(DateOnly day, TimeZoneInfo timeZone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, timeZone.GetUtcOffset(local));
    }

    /// <summary>"yyyy-MM-ddTHH:mm:ss" on the user's clock; a time with an offset or Z is that instant.</summary>
    private static DateTimeOffset Parse(string text, TimeZoneInfo timeZone)
    {
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            throw new InvalidOperationException($"'{text}' isn't a date and time like 2026-11-12T15:00:00.");
        return parsed.Kind == DateTimeKind.Unspecified
            ? new DateTimeOffset(parsed, timeZone.GetUtcOffset(parsed))
            : TimeZoneInfo.ConvertTime(new DateTimeOffset(parsed.ToUniversalTime(), TimeSpan.Zero), timeZone);
    }

    public void Dispose() => _dav.Dispose();
}
