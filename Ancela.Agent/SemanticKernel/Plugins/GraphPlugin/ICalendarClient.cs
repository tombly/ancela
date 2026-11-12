namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

/// <summary>
/// The owner's calendar, as the calendar tools use it: Microsoft 365 through Graph (<see cref="GraphClient"/>)
/// or iCloud over CalDAV (<see cref="CalDavCalendarClient"/>), as CALENDAR_PROVIDER says (DependencyModule).
/// </summary>
public interface ICalendarClient
{
    /// <summary>Every occurrence in every calendar that overlaps the range, repeating events expanded, earliest first.</summary>
    Task<EventModel[]> GetUserEventsAsync(DateTimeOffset start, DateTimeOffset end, TimeZoneInfo timeZone);

    /// <summary>A new event; <paramref name="start"/> and <paramref name="end"/> are "yyyy-MM-ddTHH:mm:ss" on the user's clock.</summary>
    Task<EventModel> CreateEventAsync(string subject, string start, string end, TimeZoneInfo timeZone, string? location = null, string? body = null, bool isAllDay = false);
}
