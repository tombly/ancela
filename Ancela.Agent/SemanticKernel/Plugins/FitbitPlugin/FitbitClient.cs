using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Ancela.Agent.SemanticKernel.Plugins.FitbitPlugin.Models;
using Microsoft.Azure.Cosmos;

namespace Ancela.Agent.SemanticKernel.Plugins.FitbitPlugin;

/// <summary>
/// Reads the owner's activity, sleep, and heart-rate summaries from the Fitbit Web API and owns the
/// OAuth 2.0 token lifecycle.
///
/// Access tokens last ~8h and refresh tokens are single-use (each refresh rotates the pair), which
/// makes a double-refresh destructive — the second one fails and can invalidate the stored token.
/// Refreshes are therefore serialized with a <see cref="SemaphoreSlim"/> (one per process) and the
/// rotated token is written to Cosmos with optimistic concurrency (one across processes): a losing
/// writer re-reads and reuses the winner's token rather than refreshing again.
///
/// The first refresh token is seeded once from <see cref="FitbitCredentials.SeedRefreshToken"/> and
/// Cosmos is authoritative thereafter. Changing the seed re-bootstraps (the re-consent path).
/// </summary>
public class FitbitClient(
    IHttpClientFactory _httpClientFactory,
    IFitbitTokenStore _tokens,
    FitbitCredentials _credentials)
{
    public const string ClientName = "fitbit";
    private const string FitbitProvider = "fitbit";

    // Refresh a little before the stated expiry to absorb clock skew and request latency.
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private OAuthToken? _cached;

    // ---- Data surface ------------------------------------------------------

    public async Task<DailyActivityModel> GetDailyActivityAsync(string date)
    {
        var response = await GetAsync<ActivityResponse>($"/1/user/-/activities/date/{date}.json");
        var summary = response.Summary ?? new ActivitySummary();
        return new DailyActivityModel
        {
            Date = date,
            Steps = summary.Steps,
            StepsGoal = response.Goals?.Steps,
            Distance = summary.Distances?.FirstOrDefault(d => d.Activity == "total")?.Distance ?? 0,
            CaloriesOut = summary.CaloriesOut,
            VeryActiveMinutes = summary.VeryActiveMinutes,
            FairlyActiveMinutes = summary.FairlyActiveMinutes,
            LightlyActiveMinutes = summary.LightlyActiveMinutes,
            SedentaryMinutes = summary.SedentaryMinutes,
        };
    }

    public async Task<SleepSummaryModel> GetSleepSummaryAsync(string date)
    {
        var response = await GetAsync<SleepResponse>($"/1.2/user/-/sleep/date/{date}.json");
        var summary = response.Summary ?? new SleepSummary();
        var main = response.Sleep?.FirstOrDefault(s => s.IsMainSleep) ?? response.Sleep?.FirstOrDefault();
        return new SleepSummaryModel
        {
            Date = date,
            AsleepMinutes = summary.TotalMinutesAsleep,
            InBedMinutes = summary.TotalTimeInBed,
            Efficiency = main?.Efficiency,
            Stages = summary.Stages is { } s
                ? new SleepStages { DeepMinutes = s.Deep, LightMinutes = s.Light, RemMinutes = s.Rem, AwakeMinutes = s.Wake }
                : null,
        };
    }

    public async Task<HeartRateModel[]> GetHeartRateAsync(string startDate, string endDate)
    {
        var response = await GetAsync<HeartResponse>($"/1/user/-/activities/heart/date/{startDate}/{endDate}.json");
        return (response.Days ?? [])
            .Select(d => new HeartRateModel
            {
                Date = d.DateTime,
                RestingHeartRate = d.Value?.RestingHeartRate,
                Zones = (d.Value?.HeartRateZones ?? [])
                    .Select(z => new HeartRateZone { Name = z.Name, Min = z.Min, Max = z.Max, Minutes = z.Minutes })
                    .ToArray(),
            })
            .ToArray();
    }

    // ---- HTTP with auth + a single 401 retry -------------------------------

    private async Task<T> GetAsync<T>(string path)
    {
        var accessToken = await GetAccessTokenAsync();
        using (var response = await SendAsync(path, accessToken))
        {
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                response.EnsureSuccessStatusCode();
                return (await response.Content.ReadFromJsonAsync<T>())!;
            }
        }

        // The token may have been revoked before its stated expiry — force one refresh and retry.
        accessToken = await GetAccessTokenAsync(staleAccessToken: accessToken);
        using var retry = await SendAsync(path, accessToken);
        retry.EnsureSuccessStatusCode();
        return (await retry.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<HttpResponseMessage> SendAsync(string path, string accessToken)
    {
        var client = _httpClientFactory.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request);
    }

    // ---- Token lifecycle ---------------------------------------------------

    /// <param name="staleAccessToken">
    /// When set, forces a refresh of this token unless another caller already replaced it (used
    /// after a 401). When null, the current token is reused while it is still fresh.
    /// </param>
    private async Task<string> GetAccessTokenAsync(string? staleAccessToken = null)
    {
        if (CanReuseCache(staleAccessToken))
            return _cached!.AccessToken;

        await _tokenLock.WaitAsync();
        try
        {
            // Re-check the in-process cache now that we hold the lock — another caller may have
            // refreshed while we waited.
            if (CanReuseCache(staleAccessToken))
                return _cached!.AccessToken;

            var doc = await _tokens.GetAsync();
            var seedFingerprint = _credentials.SeedRefreshToken is { Length: > 0 } seed ? Fingerprint(seed) : null;

            // Bootstrap (no document) or re-consent (the env seed changed): the seed is authoritative.
            if (doc is null || (seedFingerprint is not null && doc.SeedFingerprint != seedFingerprint))
            {
                if (string.IsNullOrWhiteSpace(_credentials.SeedRefreshToken))
                    throw new InvalidOperationException(
                        "Fitbit is not connected. Run `ancela fitbit auth` and set FITBIT_REFRESH_TOKEN.");

                var bootstrapped = await ExchangeRefreshTokenAsync(_credentials.SeedRefreshToken);
                bootstrapped.SeedFingerprint = seedFingerprint;
                bootstrapped.ETag = doc?.ETag; // overwrite a stale-seed document if one exists
                return (await PersistAsync(bootstrapped)).AccessToken;
            }

            // Document matches the seed. Reuse it unless near expiry or we're forcing a refresh.
            if (staleAccessToken is null && IsFresh(doc))
                return (_cached = doc).AccessToken;
            if (staleAccessToken is not null && doc.AccessToken != staleAccessToken)
                return (_cached = doc).AccessToken; // someone else already refreshed

            var refreshed = await ExchangeRefreshTokenAsync(doc.RefreshToken);
            refreshed.SeedFingerprint = doc.SeedFingerprint;
            refreshed.ETag = doc.ETag;
            return (await PersistAsync(refreshed)).AccessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private bool CanReuseCache(string? staleAccessToken) => staleAccessToken is null
        ? _cached is not null && IsFresh(_cached)
        : _cached is not null && _cached.AccessToken != staleAccessToken;

    private async Task<OAuthToken> PersistAsync(OAuthToken token)
    {
        try
        {
            await _tokens.SaveAsync(token);
            return _cached = token;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            // Another instance refreshed concurrently. Its rotated token is the live one; the token
            // we just minted is already invalid (single-use), so reuse theirs instead of refreshing.
            var current = await _tokens.GetAsync()
                ?? throw new InvalidOperationException("Fitbit token document vanished during a concurrent refresh.");
            return _cached = current;
        }
    }

    private async Task<OAuthToken> ExchangeRefreshTokenAsync(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(_credentials.ClientId) || string.IsNullOrWhiteSpace(_credentials.ClientSecret))
            throw new InvalidOperationException("FITBIT_CLIENT_ID / FITBIT_CLIENT_SECRET are not configured.");

        var client = _httpClientFactory.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth2/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
            }),
        };
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_credentials.ClientId}:{_credentials.ClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TokenResponse>()
            ?? throw new InvalidOperationException("Fitbit token endpoint returned an empty body.");

        return new OAuthToken
        {
            Id = FitbitProvider,
            Provider = FitbitProvider,
            AccessToken = body.AccessToken,
            RefreshToken = body.RefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(body.ExpiresIn),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static bool IsFresh(OAuthToken token) => token.ExpiresAt > DateTimeOffset.UtcNow.Add(ExpirySkew);

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    // ---- Fitbit API response DTOs (deserialization only) -------------------

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private sealed class ActivityResponse
    {
        [JsonPropertyName("summary")] public ActivitySummary? Summary { get; set; }
        [JsonPropertyName("goals")] public ActivityGoals? Goals { get; set; }
    }

    private sealed class ActivityGoals
    {
        [JsonPropertyName("steps")] public int? Steps { get; set; }
    }

    private sealed class ActivitySummary
    {
        [JsonPropertyName("steps")] public int Steps { get; set; }
        [JsonPropertyName("distances")] public ActivityDistance[]? Distances { get; set; }
        [JsonPropertyName("caloriesOut")] public int CaloriesOut { get; set; }
        [JsonPropertyName("veryActiveMinutes")] public int VeryActiveMinutes { get; set; }
        [JsonPropertyName("fairlyActiveMinutes")] public int FairlyActiveMinutes { get; set; }
        [JsonPropertyName("lightlyActiveMinutes")] public int LightlyActiveMinutes { get; set; }
        [JsonPropertyName("sedentaryMinutes")] public int SedentaryMinutes { get; set; }
    }

    private sealed class ActivityDistance
    {
        [JsonPropertyName("activity")] public string Activity { get; set; } = "";
        [JsonPropertyName("distance")] public double Distance { get; set; }
    }

    private sealed class SleepResponse
    {
        [JsonPropertyName("sleep")] public SleepRecord[]? Sleep { get; set; }
        [JsonPropertyName("summary")] public SleepSummary? Summary { get; set; }
    }

    private sealed class SleepRecord
    {
        [JsonPropertyName("isMainSleep")] public bool IsMainSleep { get; set; }
        [JsonPropertyName("efficiency")] public int Efficiency { get; set; }
    }

    private sealed class SleepSummary
    {
        [JsonPropertyName("totalMinutesAsleep")] public int TotalMinutesAsleep { get; set; }
        [JsonPropertyName("totalTimeInBed")] public int TotalTimeInBed { get; set; }
        [JsonPropertyName("stages")] public SleepStageMinutes? Stages { get; set; }
    }

    private sealed class SleepStageMinutes
    {
        [JsonPropertyName("deep")] public int Deep { get; set; }
        [JsonPropertyName("light")] public int Light { get; set; }
        [JsonPropertyName("rem")] public int Rem { get; set; }
        [JsonPropertyName("wake")] public int Wake { get; set; }
    }

    private sealed class HeartResponse
    {
        [JsonPropertyName("activities-heart")] public HeartDay[]? Days { get; set; }
    }

    private sealed class HeartDay
    {
        [JsonPropertyName("dateTime")] public string DateTime { get; set; } = "";
        [JsonPropertyName("value")] public HeartValue? Value { get; set; }
    }

    private sealed class HeartValue
    {
        [JsonPropertyName("restingHeartRate")] public int? RestingHeartRate { get; set; }
        [JsonPropertyName("heartRateZones")] public HeartZone[]? HeartRateZones { get; set; }
    }

    private sealed class HeartZone
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("min")] public int Min { get; set; }
        [JsonPropertyName("max")] public int Max { get; set; }
        [JsonPropertyName("minutes")] public int Minutes { get; set; }
    }
}
