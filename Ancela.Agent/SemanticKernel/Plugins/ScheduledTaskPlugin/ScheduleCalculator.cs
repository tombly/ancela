using System.Globalization;

namespace Ancela.Agent.SemanticKernel.Plugins.ScheduledTaskPlugin;

/// <summary>
/// Computes the next UTC fire time for a recurring scheduled task expressed as a local
/// time-of-day on a set of weekdays in the user's IANA timezone. Pure and side-effect free
/// so the timezone/DST math can be unit-tested in isolation.
/// </summary>
public static class ScheduleCalculator
{
    /// <summary>
    /// Returns the next instant strictly after <paramref name="utcNow"/> that matches the
    /// given local time-of-day on one of the given weekdays, in the given timezone.
    /// </summary>
    public static DateTimeOffset NextRun(string timeOfDay, IReadOnlyCollection<DayOfWeek> days, string ianaTimeZoneId, DateTimeOffset utcNow)
    {
        if (days is null || days.Count == 0)
            throw new ArgumentException("At least one day of week is required.", nameof(days));
        if (!TryParseTimeOfDay(timeOfDay, out var tod))
            throw new ArgumentException($"timeOfDay must be in HH:mm 24-hour format; got '{timeOfDay}'.", nameof(timeOfDay));

        var tz = TimeZoneInfo.FindSystemTimeZoneById(ianaTimeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(utcNow, tz);

        // Walk forward up to 7 days; index 7 covers "same weekday next week" when today's
        // slot has already passed.
        for (var dayOffset = 0; dayOffset <= 7; dayOffset++)
        {
            var date = localNow.Date.AddDays(dayOffset);
            if (!days.Contains(date.DayOfWeek))
                continue;

            var localCandidate = date + tod; // DateTimeKind.Unspecified, interpreted in tz below

            // Spring-forward: this wall-clock time doesn't exist on this date. Nudge past the gap.
            if (tz.IsInvalidTime(localCandidate))
                localCandidate = localCandidate.AddHours(1);

            var candidate = new DateTimeOffset(localCandidate, tz.GetUtcOffset(localCandidate));
            if (candidate.ToUniversalTime() > utcNow)
                return candidate.ToUniversalTime();
        }

        throw new InvalidOperationException("Could not compute a next run within 7 days; check the schedule.");
    }

    public static bool TryParseTimeOfDay(string timeOfDay, out TimeSpan result)
    {
        result = default;
        if (TimeOnly.TryParseExact(timeOfDay, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            result = time.ToTimeSpan();
            return true;
        }
        return false;
    }
}
