using System.Net;
using System.Text;
using System.Text.Json;
using Ancela.Agent.SemanticKernel.Plugins.WeatherPlugin;
using FluentAssertions;
using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// Unit tests for the weather client: the two-hop resolve (geocode then NWS grid), picking today's
/// daytime high rather than a nighttime low, caching, and the failure modes. HTTP is faked with a
/// routing stub — no live api.weather.gov.
/// </summary>
public class WeatherClientTests
{
    private const string ForecastUrl = "https://api.weather.gov/gridpoints/SEW/131,79/forecast";

    [Fact]
    public async Task PicksTodaysDaytimeHigh_NotTheNighttimeLow()
    {
        // The bug this tool replaces: on 2026-10-11 the morning summary reported 35°F, which was
        // near the overnight low, when the daytime high was around 63.
        var (client, _) = Build(Periods(
            ("Today", true, 63, "Sunny", "2026-10-11T06:00:00-07:00"),
            ("Tonight", false, 37, "Clear", "2026-10-11T18:00:00-07:00"),
            ("Monday", true, 61, "Sunny", "2026-10-12T06:00:00-07:00")));

        var forecast = await client.GetForecastAsync("Mill Creek, WA");

        forecast.TodayHigh.Should().Be(63);
        forecast.TonightLow.Should().Be(37);
        forecast.TodayConditions.Should().Be("Sunny");
        forecast.TemperatureUnit.Should().Be("F");
    }

    [Fact]
    public async Task WhenTodaysDaytimePeriodHasPassed_TodayHighIsNull()
    {
        // An evening call: NWS starts the list at the current period, so there is no daytime
        // period left for today. Reporting the nighttime number as a high is the whole bug, so
        // this must be null rather than a guess.
        var (client, _) = Build(Periods(
            ("Tonight", false, 39, "Clear", "2026-10-11T18:00:00-07:00"),
            ("Columbus Day", true, 61, "Sunny", "2026-10-12T06:00:00-07:00")));

        var forecast = await client.GetForecastAsync("Mill Creek, WA");

        forecast.TodayHigh.Should().BeNull();
        forecast.TonightLow.Should().Be(39);
    }

    [Fact]
    public async Task ReportsWhatTheGeocoderMatched()
    {
        // "Mill Creek" alone matches West Virginia and Pennsylvania ahead of Washington, so the
        // resolved name is always surfaced — a silently wrong location is the failure to avoid.
        var (client, _) = Build(Periods(("Today", true, 63, "Sunny", "2026-10-11T06:00:00-07:00")));

        var forecast = await client.GetForecastAsync("Mill Creek, WA");

        forecast.ResolvedLocation.Should().Be("Mill Creek, Washington, US");
    }

    [Fact]
    public async Task SendsTheLocationVerbatimToTheGeocoder()
    {
        // The comma form matters: "Mill Creek Washington" returns no results from this geocoder,
        // while "Mill Creek, WA" ranks the right town first.
        var (client, handler) = Build(Periods(("Today", true, 63, "Sunny", "2026-10-11T06:00:00-07:00")));

        await client.GetForecastAsync("Mill Creek, WA");

        // Asserted on the decoded query rather than the escaped form: Uri.ToString() turns %20
        // back into spaces while leaving the comma encoded, so matching raw escaping is brittle.
        handler.Requests.Should().Contain(u =>
            u.Contains("/v1/search") && Uri.UnescapeDataString(u).Contains("name=Mill Creek, WA"));
    }

    [Fact]
    public async Task ResolvesLocationOnce_ThenReusesIt()
    {
        var (client, handler) = Build(Periods(("Today", true, 63, "Sunny", "2026-10-11T06:00:00-07:00")));

        await client.GetForecastAsync("Mill Creek, WA");
        await client.GetForecastAsync("Mill Creek, WA");

        handler.Requests.Count(u => u.Contains("/v1/search")).Should().Be(1);
        handler.Requests.Count(u => u.Contains("/points/")).Should().Be(1);
        handler.Requests.Count(u => u.Contains("/forecast")).Should().Be(2, "only the forecast is re-fetched");
    }

    [Fact]
    public async Task UnknownLocation_SaysToIncludeTheState()
    {
        var (client, _) = Build(Periods(("Today", true, 63, "Sunny", "2026-10-11T06:00:00-07:00")));
        FakeHandler.Current!.GeocodeJson = """{"generationtime_ms":0.1}""";

        var act = async () => await client.GetForecastAsync("Nowhereville");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("Include the state");
    }

    [Fact]
    public async Task ForecastFailure_NamesTheEndpoint_ButNotTheGridUrl()
    {
        // NWS forecast URLs embed the grid square the coordinates fall in, and these messages
        // reach the audit log.
        var (client, _) = Build(Periods(("Today", true, 63, "Sunny", "2026-10-11T06:00:00-07:00")));
        FakeHandler.Current!.ForecastStatus = HttpStatusCode.ServiceUnavailable;

        var act = async () => await client.GetForecastAsync("Mill Creek, WA");

        var message = (await act.Should().ThrowAsync<HttpRequestException>()).Which.Message;
        message.Should().Contain("forecast").And.Contain("503");
        message.Should().NotContain("SEW/131,79");
    }

    [Fact]
    public async Task EmptyLocation_IsRejectedWithoutCallingOut()
    {
        var (client, handler) = Build(Periods(("Today", true, 63, "Sunny", "2026-10-11T06:00:00-07:00")));

        var act = async () => await client.GetForecastAsync("  ");

        await act.Should().ThrowAsync<ArgumentException>();
        handler.Requests.Should().BeEmpty();
    }

    private static string Periods(params (string Name, bool IsDaytime, int Temp, string Short, string Start)[] periods) =>
        JsonSerializer.Serialize(new
        {
            properties = new
            {
                periods = periods.Select((p, i) => new
                {
                    number = i + 1,
                    name = p.Name,
                    isDaytime = p.IsDaytime,
                    temperature = p.Temp,
                    temperatureUnit = "F",
                    shortForecast = p.Short,
                    startTime = p.Start,
                }).ToArray(),
            },
        });

    private static (WeatherClient Client, FakeHandler Handler) Build(string forecastJson)
    {
        var handler = new FakeHandler { ForecastJson = forecastJson };
        FakeHandler.Current = handler;

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(WeatherClient.GeocodeClientName))
            .Returns(() => new HttpClient(handler) { BaseAddress = new Uri("https://geocoding-api.open-meteo.com") });
        factory.Setup(f => f.CreateClient(WeatherClient.NwsClientName))
            .Returns(() => new HttpClient(handler) { BaseAddress = new Uri("https://api.weather.gov") });

        return (new WeatherClient(factory.Object), handler);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        [ThreadStatic] public static FakeHandler? Current;

        public List<string> Requests { get; } = [];

        public string GeocodeJson { get; set; } = JsonSerializer.Serialize(new
        {
            results = new[]
            {
                new { name = "Mill Creek", latitude = 47.8601, longitude = -122.2043, admin1 = "Washington", country_code = "US" },
            },
        });
        public string PointsJson { get; set; } = JsonSerializer.Serialize(new { properties = new { forecast = ForecastUrl } });
        public string ForecastJson { get; set; } = "{}";
        public HttpStatusCode ForecastStatus { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            Requests.Add(uri);

            if (uri.Contains("/v1/search")) return Task.FromResult(Respond(GeocodeJson));
            if (uri.Contains("/points/")) return Task.FromResult(Respond(PointsJson));
            if (uri.Contains("/forecast"))
                return Task.FromResult(ForecastStatus == HttpStatusCode.OK
                    ? Respond(ForecastJson)
                    : Respond("upstream unavailable", ForecastStatus));

            return Task.FromResult(Respond("""{"message":"unrouted"}""", HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Respond(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
