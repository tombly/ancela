namespace Ancela.Agent.SemanticKernel.Plugins.WeatherPlugin.Models;

/// <summary>
/// One National Weather Service forecast period. NWS splits the week into alternating daytime and
/// nighttime periods, each carrying a single temperature: the high for a daytime period, the low
/// for a nighttime one. <see cref="IsDaytime"/> is what says which — the distinction that search
/// result snippets leave to the reader, and get wrong.
/// </summary>
public record ForecastPeriod(
    string Name,
    bool IsDaytime,
    int Temperature,
    string TemperatureUnit,
    string ShortForecast,
    DateTimeOffset StartTime);

/// <summary>
/// Today's forecast for a location.
/// </summary>
/// <param name="ResolvedLocation">
/// What the geocoder actually matched, e.g. "Mill Creek, Washington, US". Always reported, because
/// place names are ambiguous — "Mill Creek" alone matches towns in West Virginia and Pennsylvania
/// ahead of the Washington one — and a silently wrong location is exactly the failure this tool
/// exists to prevent.
/// </param>
/// <param name="TodayHigh">
/// Today's daytime high, or null when today's daytime period has already passed (the forecast runs
/// from the current period onward, so an evening call has no daytime period left for today).
/// </param>
/// <param name="TonightLow">Tonight's low, or null if no nighttime period falls on today's date.</param>
/// <param name="Upcoming">The next few periods, for questions beyond today.</param>
public record WeatherForecast(
    string ResolvedLocation,
    string TemperatureUnit,
    int? TodayHigh,
    int? TonightLow,
    string? TodayConditions,
    ForecastPeriod[] Upcoming);
