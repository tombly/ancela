using Ancela.Agent.SemanticKernel.Plugins.WeatherPlugin;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Ancela.Agent.Tests;

/// <summary>
/// Live diagnostic against the real Open-Meteo geocoder and api.weather.gov. Excluded from normal
/// runs (Integration trait) but, unlike the UniFi and Google Health live tests, needs no API key:
///   dotnet test --filter "FullyQualifiedName~WeatherLiveTests"
/// Exercises the whole chain — geocode, grid lookup, forecast, and the daytime/nighttime split —
/// so the JSON binding is covered rather than just the URL shapes.
/// </summary>
[Trait("Category", "Integration")]
public class WeatherLiveTests(ITestOutputHelper _output)
{
    [Fact]
    public async Task ReadsARealForecastForMillCreek()
    {
        var client = BuildClient();

        var forecast = await client.GetForecastAsync("Mill Creek, WA");

        _output.WriteLine($"resolved : {forecast.ResolvedLocation}");
        _output.WriteLine($"today    : {(forecast.TodayHigh is int h ? $"high {h}{forecast.TemperatureUnit} — {forecast.TodayConditions}" : "daytime period already passed")}");
        _output.WriteLine($"tonight  : low {forecast.TonightLow?.ToString() ?? "(none)"}{forecast.TemperatureUnit}");
        foreach (var period in forecast.Upcoming)
            _output.WriteLine($"  {period.Name,-14} daytime={period.IsDaytime,-5} {period.Temperature}{period.TemperatureUnit}  {period.ShortForecast}");

        forecast.ResolvedLocation.Should().Contain("Washington",
            because: "a bare 'Mill Creek' matches West Virginia and Pennsylvania first");
        forecast.TemperatureUnit.Should().Be("F");
        forecast.Upcoming.Should().NotBeEmpty();

        // Whichever period is current, the one we report must match its own daytime flag: a
        // daytime high and a nighttime low can never be the same period.
        forecast.Upcoming.Should().Contain(p => p.IsDaytime)
            .And.Contain(p => !p.IsDaytime, because: "NWS alternates day and night periods");

        // Sanity bounds rather than exact values, since the real forecast moves.
        if (forecast.TodayHigh is int high)
            high.Should().BeInRange(-40, 130);
        if (forecast.TonightLow is int low)
            low.Should().BeInRange(-60, 110);
    }

    [Fact]
    public async Task UnknownLocationFailsClearly()
    {
        var client = BuildClient();

        var act = async () => await client.GetForecastAsync("Zzyzxville, QQ");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static IWeatherClient BuildClient()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(WeatherClient.NwsClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.weather.gov");
            // Without an identifying User-Agent NWS answers 403.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Ancela/1.0 (github.com/tombly/ancela)");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/geo+json");
        });
        services.AddHttpClient(WeatherClient.GeocodeClientName,
            client => client.BaseAddress = new Uri("https://geocoding-api.open-meteo.com"));

        var provider = services.BuildServiceProvider();
        return new WeatherClient(provider.GetRequiredService<IHttpClientFactory>());
    }
}
