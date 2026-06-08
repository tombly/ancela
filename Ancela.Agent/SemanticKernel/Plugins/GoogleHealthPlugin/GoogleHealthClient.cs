using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Ancela.Agent.SemanticKernel.Plugins.GoogleHealthPlugin.Models;
using Microsoft.Extensions.Logging;

namespace Ancela.Agent.SemanticKernel.Plugins.GoogleHealthPlugin;

/// <summary>
/// Reads the owner's activity, sleep, and resting-heart-rate data from the Google Health API and
/// owns the Google OAuth 2.0 token lifecycle.
///
/// Google refresh tokens are durable (reusable, generally non-expiring in production), so the token
/// layer is simple: an in-process cache guarded by a lock, plus the Cosmos document as a resilient
/// cache. A failed Cosmos write is non-fatal — the in-memory token keeps working and the stored
/// (still-valid) refresh token is reused next time. The first refresh token is seeded once from
/// <see cref="GoogleHealthCredentials.SeedRefreshToken"/>; changing the seed re-bootstraps.
/// </summary>
public class GoogleHealthClient(
    IHttpClientFactory _httpClientFactory,
    IGoogleHealthTokenStore _tokens,
    GoogleHealthCredentials _credentials,
    ILogger<GoogleHealthClient> _logger)
{
    public const string ClientName = "googlehealth";
    private const string Provider = "google-health";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    // Resting heart rate (and calorie) roll-ups are capped at a 14-day range by the API.
    private const int MaxHeartRateRangeDays = 14;

    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private OAuthToken? _cached;

    // ---- Data surface ------------------------------------------------------

    public async Task<DailyActivityModel> GetDailyActivityAsync(string date)
    {
        var day = ResolveDate(date);
        var range = (day, day.AddDays(1));

        // One roll-up per data type (no batchGet); fire them together behind the shared token lock.
        var stepsTask = DailyRollUpAsync("steps", range);
        var distanceTask = DailyRollUpAsync("distance", range);
        var caloriesTask = DailyRollUpAsync("total-calories", range);
        var activeTask = DailyRollUpAsync("active-minutes", range);
        await Task.WhenAll(stepsTask, distanceTask, caloriesTask, activeTask);

        var byLevel = FirstValue(activeTask.Result)?.ActiveMinutes?.ByLevel ?? [];
        int LevelMinutes(string level) => (int)(byLevel
            .FirstOrDefault(b => string.Equals(b.ActivityLevel, level, StringComparison.OrdinalIgnoreCase))?
            .ActiveMinutesSum ?? 0);

        return new DailyActivityModel
        {
            Date = day.ToString("yyyy-MM-dd"),
            Steps = (int)(FirstValue(stepsTask.Result)?.Steps?.CountSum ?? 0),
            DistanceKm = (FirstValue(distanceTask.Result)?.Distance?.MetersSum ?? 0) / 1000.0,
            CaloriesOut = (int)Math.Round(FirstValue(caloriesTask.Result)?.TotalCalories?.KilocaloriesSum ?? 0),
            LightActiveMinutes = LevelMinutes("LIGHT"),
            ModerateActiveMinutes = LevelMinutes("MODERATE"),
            VigorousActiveMinutes = LevelMinutes("VIGOROUS"),
        };
    }

    public async Task<SleepSummaryModel> GetSleepSummaryAsync(string date)
    {
        var day = ResolveDate(date);
        var response = await ReconcileSleepAsync(day);

        // Pick the longest session for the date (the main sleep).
        var session = (response.DataPoints ?? [])
            .Select(d => d.Sleep)
            .Where(s => s is not null)
            .OrderByDescending(s => ParseDurationMinutes(s!.Duration) ?? 0)
            .FirstOrDefault();

        var summary = new SleepSummaryModel { Date = day.ToString("yyyy-MM-dd") };
        if (session is null)
            return summary;

        var stageSummary = session.SleepSummary?.StageSummary;
        if (stageSummary is { Length: > 0 })
        {
            int StageMinutes(string stage) => stageSummary
                .Where(s => string.Equals(s.Stage, stage, StringComparison.OrdinalIgnoreCase))
                .Sum(s => ParseDurationMinutes(s.Duration) ?? 0);

            summary.Stages = new SleepStages
            {
                DeepMinutes = StageMinutes("DEEP"),
                LightMinutes = StageMinutes("LIGHT"),
                RemMinutes = StageMinutes("REM"),
                AwakeMinutes = StageMinutes("AWAKE"),
            };
            summary.AsleepMinutes = summary.Stages.DeepMinutes + summary.Stages.LightMinutes + summary.Stages.RemMinutes;
        }
        else
        {
            summary.AsleepMinutes = ParseDurationMinutes(session.Duration);
        }

        summary.InBedMinutes = ParseDurationMinutes(session.Duration);
        summary.Efficiency = session.Efficiency is { } efficiency ? (int)Math.Round(efficiency) : null;
        return summary;
    }

    public async Task<HeartRateModel[]> GetHeartRateAsync(string startDate, string endDate)
    {
        var start = ResolveDate(startDate);
        var end = ResolveDate(endDate);
        if (end < start)
            (start, end) = (end, start);
        if (end.DayNumber - start.DayNumber > MaxHeartRateRangeDays)
            throw new InvalidOperationException(
                $"The Google Health API limits resting heart rate to a {MaxHeartRateRangeDays}-day range; request a narrower window.");

        var response = await DailyRollUpAsync("daily-resting-heart-rate", (start, end.AddDays(1)));

        return (response.RollupDataPoints ?? [])
            .Select(point =>
            {
                var resting = point.Value?.DailyRestingHeartRate;
                return new HeartRateModel
                {
                    Date = FormatCivil(point.CivilStartTime) ?? start.ToString("yyyy-MM-dd"),
                    RestingHeartRate = resting?.BeatsPerMinuteAvg is { } avg ? (int)Math.Round(avg) : null,
                    Min = resting?.BeatsPerMinuteMin,
                    Max = resting?.BeatsPerMinuteMax,
                };
            })
            .ToArray();
    }

    // ---- Requests ----------------------------------------------------------

    private Task<RollupResponse> DailyRollUpAsync(string dataType, (DateOnly Start, DateOnly EndExclusive) range)
    {
        var body = new
        {
            range = new { start = Civil(range.Start), end = Civil(range.EndExclusive) },
            windowSizeDays = 1,
        };
        return SendJsonAsync<RollupResponse>(
            HttpMethod.Post, $"/v4/users/me/dataTypes/{dataType}/dataPoints:dailyRollUp", body);
    }

    private Task<ReconcileResponse> ReconcileSleepAsync(DateOnly day)
    {
        // Filter the reconciled sleep stream to the target date. The exact civil-time filter grammar
        // is provisional and should be confirmed against a live response.
        var filter = $"civilStartTime >= \"{day:yyyy-MM-dd}T00:00:00\" AND " +
                     $"civilStartTime < \"{day.AddDays(1):yyyy-MM-dd}T00:00:00\"";
        var path = $"/v4/users/me/dataTypes/sleep/dataPoints:reconcile?pageSize=25&filter={Uri.EscapeDataString(filter)}";
        return SendJsonAsync<ReconcileResponse>(HttpMethod.Get, path, body: null);
    }

    private async Task<T> SendJsonAsync<T>(HttpMethod method, string path, object? body)
    {
        var accessToken = await GetAccessTokenAsync();
        using (var first = await SendAsync(method, path, body, accessToken))
        {
            if (first.StatusCode != HttpStatusCode.Unauthorized)
            {
                first.EnsureSuccessStatusCode();
                return (await first.Content.ReadFromJsonAsync<T>())!;
            }
        }

        // Access token rejected before its stated expiry — force one refresh and retry.
        accessToken = await GetAccessTokenAsync(staleAccessToken: accessToken);
        using var retry = await SendAsync(method, path, body, accessToken);
        retry.EnsureSuccessStatusCode();
        return (await retry.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, string accessToken)
    {
        var client = _httpClientFactory.CreateClient(ClientName);
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request);
    }

    // ---- Token lifecycle ---------------------------------------------------

    private async Task<string> GetAccessTokenAsync(string? staleAccessToken = null)
    {
        if (CanReuseCache(staleAccessToken))
            return _cached!.AccessToken;

        await _tokenLock.WaitAsync();
        try
        {
            if (CanReuseCache(staleAccessToken))
                return _cached!.AccessToken;

            var doc = await _tokens.GetAsync();
            var seedFingerprint = _credentials.SeedRefreshToken is { Length: > 0 } seed ? Fingerprint(seed) : null;

            // Bootstrap (no document) or re-consent (the env seed changed).
            if (doc is null || (seedFingerprint is not null && doc.SeedFingerprint != seedFingerprint))
            {
                if (string.IsNullOrWhiteSpace(_credentials.SeedRefreshToken))
                    throw new InvalidOperationException(
                        "Google Health is not connected. Run `ancela health auth` and set GOOGLE_HEALTH_REFRESH_TOKEN.");

                var bootstrapped = await RefreshAsync(_credentials.SeedRefreshToken);
                bootstrapped.SeedFingerprint = seedFingerprint;
                bootstrapped.ETag = doc?.ETag;
                await PersistAsync(bootstrapped);
                return bootstrapped.AccessToken;
            }

            if (staleAccessToken is null && IsFresh(doc))
                return (_cached = doc).AccessToken;
            if (staleAccessToken is not null && doc.AccessToken != staleAccessToken)
                return (_cached = doc).AccessToken; // someone else already refreshed

            var refreshed = await RefreshAsync(doc.RefreshToken);
            refreshed.SeedFingerprint = doc.SeedFingerprint;
            refreshed.ETag = doc.ETag;
            await PersistAsync(refreshed);
            return refreshed.AccessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private bool CanReuseCache(string? staleAccessToken) => staleAccessToken is null
        ? _cached is not null && IsFresh(_cached)
        : _cached is not null && _cached.AccessToken != staleAccessToken;

    /// <summary>
    /// Caches the token in-process, then writes it to Cosmos best-effort. The cache is set first so
    /// a transient write failure never loses a freshly minted token — durable Google refresh tokens
    /// make the stored copy a resilient cache, not a single-use ledger.
    /// </summary>
    private async Task PersistAsync(OAuthToken token)
    {
        _cached = token;
        try
        {
            await _tokens.SaveAsync(token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist the Google Health token; continuing with the in-memory token.");
        }
    }

    private async Task<OAuthToken> RefreshAsync(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(_credentials.ClientId) || string.IsNullOrWhiteSpace(_credentials.ClientSecret))
            throw new InvalidOperationException("GOOGLE_HEALTH_CLIENT_ID / GOOGLE_HEALTH_CLIENT_SECRET are not configured.");

        var client = _httpClientFactory.CreateClient(ClientName);
        // Absolute URI → the token host, overriding the data-API BaseAddress on the named client.
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = _credentials.ClientId!,
                ["client_secret"] = _credentials.ClientSecret!,
            }),
        };

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TokenResponse>()
            ?? throw new InvalidOperationException("Google token endpoint returned an empty body.");

        return new OAuthToken
        {
            Id = Provider,
            Provider = Provider,
            AccessToken = body.AccessToken,
            // Google omits refresh_token on refresh — retain the one we used.
            RefreshToken = string.IsNullOrEmpty(body.RefreshToken) ? refreshToken : body.RefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(body.ExpiresIn),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    // ---- Helpers -----------------------------------------------------------

    private static RollupValue? FirstValue(RollupResponse response) =>
        response.RollupDataPoints?.FirstOrDefault()?.Value;

    private static bool IsFresh(OAuthToken token) => token.ExpiresAt > DateTimeOffset.UtcNow.Add(ExpirySkew);

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static DateOnly ResolveDate(string date) =>
        string.Equals(date, "today", StringComparison.OrdinalIgnoreCase)
            ? DateOnly.FromDateTime(DateTime.UtcNow)
            : DateOnly.TryParse(date, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : DateOnly.FromDateTime(DateTime.UtcNow);

    private static object Civil(DateOnly day) => new { year = day.Year, month = day.Month, day = day.Day };

    private static string? FormatCivil(CivilTime? civil) =>
        civil is null ? null : $"{civil.Year:D4}-{civil.Month:D2}-{civil.Day:D2}";

    /// <summary>Parses a protobuf Duration (e.g. "28800s") to whole minutes.</summary>
    private static int? ParseDurationMinutes(string? duration)
    {
        if (string.IsNullOrEmpty(duration))
            return null;
        var seconds = duration.EndsWith('s') ? duration[..^1] : duration;
        return double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? (int)Math.Round(value / 60)
            : null;
    }

    // ---- Google Health API response DTOs (deserialization only) ------------

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private sealed class RollupResponse
    {
        [JsonPropertyName("rollupDataPoints")] public RollupDataPoint[]? RollupDataPoints { get; set; }
    }

    private sealed class RollupDataPoint
    {
        [JsonPropertyName("civilStartTime")] public CivilTime? CivilStartTime { get; set; }
        [JsonPropertyName("value")] public RollupValue? Value { get; set; }
    }

    private sealed class CivilTime
    {
        [JsonPropertyName("year")] public int Year { get; set; }
        [JsonPropertyName("month")] public int Month { get; set; }
        [JsonPropertyName("day")] public int Day { get; set; }
    }

    private sealed class RollupValue
    {
        [JsonPropertyName("steps")] public StepsValue? Steps { get; set; }
        [JsonPropertyName("distance")] public DistanceValue? Distance { get; set; }
        [JsonPropertyName("totalCalories")] public TotalCaloriesValue? TotalCalories { get; set; }
        [JsonPropertyName("activeMinutes")] public ActiveMinutesValue? ActiveMinutes { get; set; }
        [JsonPropertyName("dailyRestingHeartRate")] public RestingHeartRateValue? DailyRestingHeartRate { get; set; }
    }

    private sealed class StepsValue
    {
        [JsonPropertyName("countSum")] public long CountSum { get; set; }
    }

    private sealed class DistanceValue
    {
        [JsonPropertyName("metersSum")] public long MetersSum { get; set; }
    }

    private sealed class TotalCaloriesValue
    {
        [JsonPropertyName("kilocaloriesSum")] public double KilocaloriesSum { get; set; }
    }

    private sealed class ActiveMinutesValue
    {
        [JsonPropertyName("activeMinutesRollupByActivityLevel")] public ActiveMinutesByLevel[]? ByLevel { get; set; }
    }

    private sealed class ActiveMinutesByLevel
    {
        [JsonPropertyName("activityLevel")] public string? ActivityLevel { get; set; }
        [JsonPropertyName("activeMinutesSum")] public long ActiveMinutesSum { get; set; }
    }

    private sealed class RestingHeartRateValue
    {
        [JsonPropertyName("beatsPerMinuteAvg")] public double? BeatsPerMinuteAvg { get; set; }
        [JsonPropertyName("beatsPerMinuteMin")] public int? BeatsPerMinuteMin { get; set; }
        [JsonPropertyName("beatsPerMinuteMax")] public int? BeatsPerMinuteMax { get; set; }
    }

    private sealed class ReconcileResponse
    {
        [JsonPropertyName("dataPoints")] public SleepDataPoint[]? DataPoints { get; set; }
    }

    private sealed class SleepDataPoint
    {
        [JsonPropertyName("sleep")] public SleepValue? Sleep { get; set; }
    }

    private sealed class SleepValue
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("duration")] public string? Duration { get; set; }
        [JsonPropertyName("efficiency")] public double? Efficiency { get; set; }
        [JsonPropertyName("sleepSummary")] public SleepSummaryDto? SleepSummary { get; set; }
    }

    private sealed class SleepSummaryDto
    {
        [JsonPropertyName("stageSummary")] public StageSummary[]? StageSummary { get; set; }
    }

    private sealed class StageSummary
    {
        [JsonPropertyName("stage")] public string? Stage { get; set; }
        [JsonPropertyName("duration")] public string? Duration { get; set; }
    }
}
