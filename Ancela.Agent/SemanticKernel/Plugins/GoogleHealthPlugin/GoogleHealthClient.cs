using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
///
/// Endpoint paths use kebab-case data type ids (e.g. <c>daily-resting-heart-rate</c>); filter
/// expressions use snake_case (e.g. <c>daily_resting_heart_rate.date</c>). The API serializes int64
/// values as JSON strings, so deserialization allows reading numbers from strings.
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
    private const int MaxHeartRateRangeDays = 14;

    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(2);

    // The API serializes int64 fields (countSum, minutes, …) as JSON strings.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

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

        var byLevel = FirstPoint(activeTask.Result)?.ActiveMinutes?.ByLevel ?? [];
        int LevelMinutes(string level) => (int)(byLevel
            .FirstOrDefault(b => string.Equals(b.ActivityLevel, level, StringComparison.OrdinalIgnoreCase))?
            .ActiveMinutesSum ?? 0);

        return new DailyActivityModel
        {
            Date = day.ToString("yyyy-MM-dd"),
            Steps = (int)(FirstPoint(stepsTask.Result)?.Steps?.CountSum ?? 0),
            DistanceKm = (FirstPoint(distanceTask.Result)?.Distance?.MillimetersSum ?? 0) / 1_000_000.0,
            CaloriesOut = (int)Math.Round(FirstPoint(caloriesTask.Result)?.TotalCalories?.KcalSum ?? 0),
            LightActiveMinutes = LevelMinutes("LIGHT"),
            ModerateActiveMinutes = LevelMinutes("MODERATE"),
            VigorousActiveMinutes = LevelMinutes("VIGOROUS"),
        };
    }

    public async Task<SleepSummaryModel> GetSleepSummaryAsync(string date)
    {
        var day = ResolveDate(date);
        // Sleep is filed under its wake (end) date; filter the reconciled stream by civil end time.
        var filter = $"sleep.interval.civil_end_time >= \"{day:yyyy-MM-dd}\" AND " +
                     $"sleep.interval.civil_end_time < \"{day.AddDays(1):yyyy-MM-dd}\"";
        var response = await GetAsync<ReconcileResponse>(
            $"/v4/users/me/dataTypes/sleep/dataPoints:reconcile?pageSize=25&filter={Uri.EscapeDataString(filter)}");

        // Pick the main (longest) session for the date.
        var session = (response.DataPoints ?? [])
            .Select(d => d.Sleep)
            .Where(s => s?.Summary is not null)
            .OrderByDescending(s => s!.Summary!.MinutesInSleepPeriod)
            .FirstOrDefault();

        var result = new SleepSummaryModel { Date = day.ToString("yyyy-MM-dd") };
        if (session?.Summary is not { } summary)
            return result;

        var stages = summary.StagesSummary ?? [];
        if (string.Equals(session.Type, "STAGES", StringComparison.OrdinalIgnoreCase) && stages.Length > 0)
        {
            // stagesSummary lists each stage twice; take the first per type rather than summing.
            int StageMinutes(string type) => stages
                .Where(s => string.Equals(s.Type, type, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Minutes)
                .FirstOrDefault();

            result.Stages = new SleepStages
            {
                DeepMinutes = StageMinutes("DEEP"),
                LightMinutes = StageMinutes("LIGHT"),
                RemMinutes = StageMinutes("REM"),
                AwakeMinutes = StageMinutes("AWAKE"),
            };
        }

        result.AsleepMinutes = summary.MinutesAsleep;
        result.InBedMinutes = summary.MinutesInSleepPeriod;
        result.Efficiency = summary.MinutesInSleepPeriod > 0
            ? (int)Math.Round(summary.MinutesAsleep * 100.0 / summary.MinutesInSleepPeriod)
            : null;
        return result;
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

        // Daily summaries support list/reconcile (not dailyRollUp); reconcile yields one merged point
        // per day (list returns a row per data source). Filter by snake_case date.
        var filter = $"daily_resting_heart_rate.date >= \"{start:yyyy-MM-dd}\" AND " +
                     $"daily_resting_heart_rate.date < \"{end.AddDays(1):yyyy-MM-dd}\"";
        var response = await GetAsync<RestingHeartRateResponse>(
            $"/v4/users/me/dataTypes/daily-resting-heart-rate/dataPoints:reconcile?pageSize=100&filter={Uri.EscapeDataString(filter)}");

        // The value is a single beatsPerMinute (JSON string), with the date nested under it.
        return (response.DataPoints ?? [])
            .Select(point => point.DailyRestingHeartRate)
            .Where(resting => resting is not null)
            .Select(resting => new HeartRateModel
            {
                Date = FormatDate(resting!.Date) ?? start.ToString("yyyy-MM-dd"),
                RestingHeartRate = resting.BeatsPerMinute,
            })
            .OrderBy(h => h.Date, StringComparer.Ordinal)
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
        return PostAsync<RollupResponse>($"/v4/users/me/dataTypes/{dataType}/dataPoints:dailyRollUp", body);
    }

    private Task<T> PostAsync<T>(string path, object body) => SendJsonAsync<T>(HttpMethod.Post, path, body);

    private Task<T> GetAsync<T>(string path) => SendJsonAsync<T>(HttpMethod.Get, path, body: null);

    private async Task<T> SendJsonAsync<T>(HttpMethod method, string path, object? body)
    {
        var accessToken = await GetAccessTokenAsync();
        using (var first = await SendAsync(method, path, body, accessToken))
        {
            if (first.StatusCode != HttpStatusCode.Unauthorized)
            {
                first.EnsureSuccessStatusCode();
                return (await first.Content.ReadFromJsonAsync<T>(JsonOptions))!;
            }
        }

        // Access token rejected before its stated expiry — force one refresh and retry.
        accessToken = await GetAccessTokenAsync(staleAccessToken: accessToken);
        using var retry = await SendAsync(method, path, body, accessToken);
        retry.EnsureSuccessStatusCode();
        return (await retry.Content.ReadFromJsonAsync<T>(JsonOptions))!;
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
        if (!response.IsSuccessStatusCode)
        {
            // Kernel-function exception messages are relayed to the model as the tool result, so the
            // agent repeats whatever we say here. invalid_grant means the refresh token expired or was
            // revoked (Google "Testing"-mode tokens last ~7 days) — give the owner the exact next step
            // instead of a bare "400 Bad Request" the model can only guess at.
            var error = await ReadTokenErrorAsync(response);
            if (string.Equals(error, "invalid_grant", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Google Health access has expired and needs to be reconnected. Re-run `ancela health auth` " +
                    "to get a new refresh token, then update the GOOGLE_HEALTH_REFRESH_TOKEN secret and redeploy.");
            throw new InvalidOperationException(
                $"Google Health token refresh failed ({(int)response.StatusCode}: {error ?? "unknown_error"}).");
        }
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

    // Reads the OAuth error code (e.g. "invalid_grant") from a failed token response; null if absent.
    private static async Task<string?> ReadTokenErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<TokenErrorResponse>();
            return error?.Error;
        }
        catch
        {
            return null;
        }
    }

    private static RollupDataPoint? FirstPoint(RollupResponse response) =>
        response.RollupDataPoints?.FirstOrDefault();

    private static bool IsFresh(OAuthToken token) => token.ExpiresAt > DateTimeOffset.UtcNow.Add(ExpirySkew);

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static DateOnly ResolveDate(string date) =>
        string.Equals(date, "today", StringComparison.OrdinalIgnoreCase)
            ? DateOnly.FromDateTime(DateTime.UtcNow)
            : DateOnly.TryParse(date, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : DateOnly.FromDateTime(DateTime.UtcNow);

    // CivilDateTime nests a google.type.Date under "date"; "time" defaults to midnight when omitted.
    private static object Civil(DateOnly day) => new { date = new { year = day.Year, month = day.Month, day = day.Day } };

    private static string? FormatDate(CivilDate? date) =>
        date is null ? null : $"{date.Year:D4}-{date.Month:D2}-{date.Day:D2}";

    // ---- Google Health API response DTOs (deserialization only) ------------

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private sealed class TokenErrorResponse
    {
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
    }

    private sealed class CivilDateTime
    {
        [JsonPropertyName("date")] public CivilDate? Date { get; set; }
    }

    private sealed class CivilDate
    {
        [JsonPropertyName("year")] public int Year { get; set; }
        [JsonPropertyName("month")] public int Month { get; set; }
        [JsonPropertyName("day")] public int Day { get; set; }
    }

    // Roll-up: the data-type field sits directly on the data point (no "value" wrapper).
    private sealed class RollupResponse
    {
        [JsonPropertyName("rollupDataPoints")] public RollupDataPoint[]? RollupDataPoints { get; set; }
    }

    private sealed class RollupDataPoint
    {
        [JsonPropertyName("civilStartTime")] public CivilDateTime? CivilStartTime { get; set; }
        [JsonPropertyName("steps")] public StepsValue? Steps { get; set; }
        [JsonPropertyName("distance")] public DistanceValue? Distance { get; set; }
        [JsonPropertyName("totalCalories")] public TotalCaloriesValue? TotalCalories { get; set; }
        [JsonPropertyName("activeMinutes")] public ActiveMinutesValue? ActiveMinutes { get; set; }
    }

    private sealed class StepsValue
    {
        [JsonPropertyName("countSum")] public long CountSum { get; set; }
    }

    private sealed class DistanceValue
    {
        [JsonPropertyName("millimetersSum")] public long MillimetersSum { get; set; }
    }

    private sealed class TotalCaloriesValue
    {
        [JsonPropertyName("kcalSum")] public double KcalSum { get; set; }
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
        [JsonPropertyName("summary")] public SleepSummaryDto? Summary { get; set; }
    }

    private sealed class SleepSummaryDto
    {
        [JsonPropertyName("minutesInSleepPeriod")] public int MinutesInSleepPeriod { get; set; }
        [JsonPropertyName("minutesAsleep")] public int MinutesAsleep { get; set; }
        [JsonPropertyName("minutesAwake")] public int MinutesAwake { get; set; }
        [JsonPropertyName("stagesSummary")] public StageSummary[]? StagesSummary { get; set; }
    }

    private sealed class StageSummary
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("minutes")] public int Minutes { get; set; }
    }

    // reconcile: dataPoints[].dailyRestingHeartRate = { date, beatsPerMinute, … } — one per day.
    private sealed class RestingHeartRateResponse
    {
        [JsonPropertyName("dataPoints")] public RestingHeartRateDataPoint[]? DataPoints { get; set; }
    }

    private sealed class RestingHeartRateDataPoint
    {
        [JsonPropertyName("dailyRestingHeartRate")] public RestingHeartRateValue? DailyRestingHeartRate { get; set; }
    }

    private sealed class RestingHeartRateValue
    {
        [JsonPropertyName("date")] public CivilDate? Date { get; set; }
        [JsonPropertyName("beatsPerMinute")] public int? BeatsPerMinute { get; set; }
    }
}
