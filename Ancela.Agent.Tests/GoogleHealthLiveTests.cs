using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ancela.Agent.SemanticKernel.Plugins.GoogleHealthPlugin;
using Ancela.Agent.SemanticKernel.Plugins.GoogleHealthPlugin.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Ancela.Agent.Tests;

/// <summary>
/// Live diagnostic against the real Google Health API. Excluded from normal runs (Integration trait)
/// and skips unless GOOGLE_HEALTH_CLIENT_ID / GOOGLE_HEALTH_CLIENT_SECRET / GOOGLE_HEALTH_REFRESH_TOKEN
/// are set. Dumps the raw request/response for each endpoint plus the mapped models to
/// <c>/tmp/gh-live-dump.txt</c> so the request shapes and response field names can be confirmed
/// against real data:
///   GOOGLE_HEALTH_CLIENT_ID=… GOOGLE_HEALTH_CLIENT_SECRET=… GOOGLE_HEALTH_REFRESH_TOKEN=… \
///   dotnet test --filter "FullyQualifiedName~GoogleHealthLiveTests"
/// </summary>
[Trait("Category", "Integration")]
public class GoogleHealthLiveTests(ITestOutputHelper _output)
{
    private const string DumpPath = "/tmp/gh-live-dump.txt";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [Fact]
    public async Task DumpRealResponses()
    {
        var credentials = GoogleHealthCredentials.FromEnvironment();
        if (string.IsNullOrWhiteSpace(credentials.ClientId)
            || string.IsNullOrWhiteSpace(credentials.ClientSecret)
            || string.IsNullOrWhiteSpace(credentials.SeedRefreshToken))
        {
            _output.WriteLine("Skipped: set GOOGLE_HEALTH_CLIENT_ID / _SECRET / _REFRESH_TOKEN to run.");
            return;
        }

        var log = new StringBuilder();
        void Log(string line) { log.AppendLine(line); _output.WriteLine(line); }

        using var http = new HttpClient();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // 1. Refresh the seed → access token.
        using var tokenResponse = await http.PostAsync("https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = credentials.SeedRefreshToken!,
                ["client_id"] = credentials.ClientId!,
                ["client_secret"] = credentials.ClientSecret!,
            }));
        var tokenBody = await tokenResponse.Content.ReadAsStringAsync();
        var tokenJson = JsonDocument.Parse(tokenBody).RootElement;
        Log($"== TOKEN {(int)tokenResponse.StatusCode} == scope={Try(tokenJson, "scope")} expires_in={Try(tokenJson, "expires_in")} has_access={tokenJson.TryGetProperty("access_token", out _)}");
        if (!tokenResponse.IsSuccessStatusCode)
        {
            Log(tokenBody);
            await WriteDumpAsync(log);
            Assert.Fail($"Token refresh failed; see {DumpPath}");
        }
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokenJson.GetProperty("access_token").GetString());

        // Query the last week so populated data reveals the real response shapes.
        var weekAgo = today.AddDays(-7);
        var tomorrow = today.AddDays(1);
        const string Base = "https://health.googleapis.com/v4/users/me/dataTypes";
        var body7d = JsonSerializer.Serialize(new
        {
            range = new
            {
                start = new { date = new { year = weekAgo.Year, month = weekAgo.Month, day = weekAgo.Day } },
                end = new { date = new { year = tomorrow.Year, month = tomorrow.Month, day = tomorrow.Day } },
            },
            windowSizeDays = 1,
        });

        foreach (var type in new[] { "steps", "distance", "total-calories", "active-minutes" })
        {
            Log($"\n== POST {type}:dailyRollUp (7d) ==");
            await DumpAsync(http, Log, HttpMethod.Post, $"{Base}/{type}/dataPoints:dailyRollUp", body7d);
        }

        var sleepFilter = Uri.EscapeDataString(
            $"sleep.interval.civil_end_time >= \"{weekAgo:yyyy-MM-dd}\" AND sleep.interval.civil_end_time < \"{tomorrow:yyyy-MM-dd}\"");
        Log("\n== GET sleep:reconcile (7d) ==");
        await DumpAsync(http, Log, HttpMethod.Get, $"{Base}/sleep/dataPoints:reconcile?pageSize=25&filter={sleepFilter}", body: null);

        // Resting HR: settle the filter (snake vs camel) and learn the response shape.
        var snake = Uri.EscapeDataString($"daily_resting_heart_rate.date >= \"{weekAgo:yyyy-MM-dd}\" AND daily_resting_heart_rate.date < \"{tomorrow:yyyy-MM-dd}\"");
        var camel = Uri.EscapeDataString($"dailyRestingHeartRate.date >= \"{weekAgo:yyyy-MM-dd}\" AND dailyRestingHeartRate.date < \"{tomorrow:yyyy-MM-dd}\"");
        Log("\n== GET daily-resting-heart-rate:list (snake) ==");
        await DumpAsync(http, Log, HttpMethod.Get, $"{Base}/daily-resting-heart-rate/dataPoints?pageSize=100&filter={snake}", body: null);
        Log("\n== GET daily-resting-heart-rate:reconcile (snake) ==");
        await DumpAsync(http, Log, HttpMethod.Get, $"{Base}/daily-resting-heart-rate/dataPoints:reconcile?pageSize=100&filter={snake}", body: null);
        Log("\n== GET daily-resting-heart-rate:list (camel) ==");
        await DumpAsync(http, Log, HttpMethod.Get, $"{Base}/daily-resting-heart-rate/dataPoints?pageSize=100&filter={camel}", body: null);

        // Mapped output via the real client for a day with data — verifies the fixed mapping.
        var client = BuildClient(credentials);
        await DumpMappedAsync(Log, "GetDailyActivityAsync(2026-06-04)", () => client.GetDailyActivityAsync("2026-06-04"));
        await DumpMappedAsync(Log, "GetSleepSummaryAsync(2026-06-04)", () => client.GetSleepSummaryAsync("2026-06-04"));
        await DumpMappedAsync(Log, "GetHeartRateAsync(7d)", () => client.GetHeartRateAsync(weekAgo.ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd")));

        await WriteDumpAsync(log);
        Log($"\nWrote {DumpPath}");
    }

    private static async Task DumpAsync(HttpClient http, Action<string> log, HttpMethod method, string url, string? body)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = body is null ? null : new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        log($"response {(int)response.StatusCode}:\n{Pretty(text)}");
    }

    private static async Task DumpMappedAsync<T>(Action<string> log, string label, Func<Task<T>> call)
    {
        try
        {
            var result = await call();
            log($"\n== mapped {label} ==\n{JsonSerializer.Serialize(result, Indented)}");
        }
        catch (Exception ex)
        {
            log($"\n== mapped {label} threw ==\n{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static GoogleHealthClient BuildClient(GoogleHealthCredentials credentials) =>
        new(new SimpleFactory(), new InMemoryStore(), credentials, NullLogger<GoogleHealthClient>.Instance);

    private static string Try(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ToString() : "(none)";

    private static string Pretty(string json)
    {
        try { return JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement, Indented); }
        catch { return json; }
    }

    private async Task WriteDumpAsync(StringBuilder log)
    {
        try { await File.WriteAllTextAsync(DumpPath, log.ToString()); }
        catch (Exception ex) { _output.WriteLine($"(could not write {DumpPath}: {ex.Message})"); }
    }

    private sealed class SimpleFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { BaseAddress = new Uri("https://health.googleapis.com") };
    }

    private sealed class InMemoryStore : IGoogleHealthTokenStore
    {
        private OAuthToken? _token;
        public Task<OAuthToken?> GetAsync() => Task.FromResult(_token);
        public Task SaveAsync(OAuthToken token) { _token = token; return Task.CompletedTask; }
    }
}
