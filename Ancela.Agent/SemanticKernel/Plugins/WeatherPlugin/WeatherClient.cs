using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Ancela.Agent.SemanticKernel.Plugins.WeatherPlugin.Models;

namespace Ancela.Agent.SemanticKernel.Plugins.WeatherPlugin;

public interface IWeatherClient
{
    Task<WeatherForecast> GetForecastAsync(string location);
}

/// <summary>
/// Reads forecasts from the National Weather Service (api.weather.gov) — free, no API key, and the
/// authoritative US source. NWS takes coordinates only, so place names are resolved first through
/// Open-Meteo's geocoder (also free and keyless).
///
/// This exists because asking the model to read a high temperature out of web search snippets does
/// not work: a snippet renders a day as "63° 37°" with no labels, and on 2026-10-11 the morning
/// summary reported 35°F — near the overnight low — when the high was around 63. NWS marks every
/// period <c>isDaytime</c> true or false, so there is nothing left to infer.
/// </summary>
public class WeatherClient(IHttpClientFactory _httpClientFactory) : IWeatherClient
{
    public const string NwsClientName = "nws";
    public const string GeocodeClientName = "open-meteo-geocode";

    /// <summary>
    /// Place name to NWS forecast URL. Both hops are stable for a given location — the geocode
    /// result and the NWS grid square it falls in — so they are resolved once per process.
    /// </summary>
    private readonly ConcurrentDictionary<string, ResolvedLocation> _locations = new(StringComparer.OrdinalIgnoreCase);

    public async Task<WeatherForecast> GetForecastAsync(string location)
    {
        if (string.IsNullOrWhiteSpace(location))
            throw new ArgumentException("A location is required, e.g. 'Mill Creek, WA'.", nameof(location));

        var resolved = await ResolveAsync(location);
        var nws = _httpClientFactory.CreateClient(NwsClientName);

        var forecast = await GetAsync<NwsForecastResponse>(nws, resolved.ForecastUrl, "forecast");
        var periods = (forecast.Properties?.Periods ?? [])
            .Select(p => new ForecastPeriod(
                p.Name ?? string.Empty,
                p.IsDaytime,
                p.Temperature,
                p.TemperatureUnit ?? "F",
                p.ShortForecast ?? string.Empty,
                p.StartTime))
            .ToArray();

        if (periods.Length == 0)
            throw new InvalidOperationException($"The National Weather Service returned no forecast periods for {resolved.Name}.");

        // "Today" means today in the location's own local time, which the period offsets carry.
        // Using UTC here would roll the day over mid-evening in Pacific time.
        var localToday = periods[0].StartTime.Date;
        var todayPeriod = periods.FirstOrDefault(p => p.IsDaytime && p.StartTime.Date == localToday);
        var tonightPeriod = periods.FirstOrDefault(p => !p.IsDaytime && p.StartTime.Date == localToday);

        return new WeatherForecast(
            resolved.Name,
            periods[0].TemperatureUnit,
            todayPeriod?.Temperature,
            tonightPeriod?.Temperature,
            todayPeriod?.ShortForecast,
            [.. periods.Take(6)]);
    }

    private async Task<ResolvedLocation> ResolveAsync(string location)
    {
        if (_locations.TryGetValue(location, out var cached))
            return cached;

        var geocode = _httpClientFactory.CreateClient(GeocodeClientName);

        // The query goes through verbatim: the user's stored home location already reads
        // "Mill Creek, WA", and that comma form is what the geocoder ranks correctly — dropping
        // the comma ("Mill Creek Washington") returns no results at all.
        var search = await GetAsync<GeocodeResponse>(
            geocode, $"/v1/search?name={Uri.EscapeDataString(location)}&count=1&country=US", "geocoder");

        var match = search.Results?.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"Could not find a US location named '{location}'. Include the state, e.g. 'Mill Creek, WA'.");

        var point = await GetAsync<NwsPointsResponse>(
            _httpClientFactory.CreateClient(NwsClientName),
            $"/points/{match.Latitude:0.####},{match.Longitude:0.####}",
            "grid lookup");

        var forecastUrl = point.Properties?.Forecast
            ?? throw new InvalidOperationException(
                $"The National Weather Service has no forecast for {match.Name} — it only covers US locations.");

        var name = string.Join(", ", new[] { match.Name, match.Admin1, match.CountryCode }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

        var resolved = new ResolvedLocation(name, forecastUrl);
        _locations[location] = resolved;
        return resolved;
    }

    /// <summary>
    /// Failures name the endpoint rather than the URL: NWS forecast URLs embed the grid square the
    /// caller's coordinates fall in, and these messages reach the audit log and can be relayed to
    /// the owner over SMS.
    /// </summary>
    private static async Task<T> GetAsync<T>(HttpClient client, string url, string endpoint)
    {
        using var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"Weather {endpoint} request failed with {(int)response.StatusCode}: {(body.Length > 300 ? body[..300] : body)}",
                null, response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<T>()
            ?? throw new InvalidOperationException($"Weather {endpoint} request returned an empty body.");
    }

    private sealed record ResolvedLocation(string Name, string ForecastUrl);

    private record GeocodeResponse(GeocodeMatch[]? Results);
    private record GeocodeMatch(string Name, double Latitude, double Longitude, string? Admin1, [property: JsonPropertyName("country_code")] string? CountryCode);
    private record NwsPointsResponse(NwsPointProperties? Properties);
    private record NwsPointProperties(string? Forecast);
    private record NwsForecastResponse(NwsForecastProperties? Properties);
    private record NwsForecastProperties(NwsPeriod[]? Periods);
    private record NwsPeriod(string? Name, bool IsDaytime, int Temperature, string? TemperatureUnit, string? ShortForecast, DateTimeOffset StartTime);
}
