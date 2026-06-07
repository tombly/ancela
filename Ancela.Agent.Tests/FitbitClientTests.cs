using System.Net;
using System.Security.Cryptography;
using System.Text;
using Ancela.Agent.SemanticKernel.Plugins.FitbitPlugin;
using Ancela.Agent.SemanticKernel.Plugins.FitbitPlugin.Models;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// Unit tests for the load-bearing part of the Fitbit integration: the OAuth token lifecycle
/// (single-use rotating refresh tokens) and response→model mapping. No live Fitbit or Cosmos —
/// HTTP is faked with a stub handler and the token store is mocked.
/// </summary>
public class FitbitClientTests
{
    private const string Seed = "seed-refresh-token";

    // ---- Token lifecycle ---------------------------------------------------

    [Fact]
    public async Task FreshToken_IsReused_WithoutRefreshing()
    {
        var store = new Mock<IFitbitTokenStore>();
        store.Setup(s => s.GetAsync()).ReturnsAsync(FreshDoc("access1"));
        var handler = new FakeFitbitHandler { DataJson = Activity(steps: 1000) };
        var client = CreateClient(handler, store.Object);

        await client.GetDailyActivityAsync("today");
        await client.GetDailyActivityAsync("today");

        handler.TokenCalls.Should().Be(0);
        store.Verify(s => s.SaveAsync(It.IsAny<OAuthToken>()), Times.Never);
        store.Verify(s => s.GetAsync(), Times.Once); // the in-process cache serves the 2nd call
        handler.SentBearers.Should().OnlyContain(b => b == "access1");
    }

    [Fact]
    public async Task NearExpiry_RefreshesOnce_AndPersistsRotatedToken()
    {
        var store = new Mock<IFitbitTokenStore>();
        store.Setup(s => s.GetAsync()).ReturnsAsync(
            Doc("access1", "refresh1", DateTimeOffset.UtcNow.AddMinutes(1), Fingerprint(Seed), etag: "etag-1"));
        OAuthToken? saved = null;
        store.Setup(s => s.SaveAsync(It.IsAny<OAuthToken>())).Callback<OAuthToken>(t => saved = t).Returns(Task.CompletedTask);

        var handler = new FakeFitbitHandler { TokenJson = () => Token("access2", "refresh2"), DataJson = Activity() };
        var client = CreateClient(handler, store.Object);

        await client.GetDailyActivityAsync("today");

        handler.TokenCalls.Should().Be(1);
        handler.SentRefreshTokens.Should().ContainSingle().Which.Should().Be("refresh1");
        saved.Should().NotBeNull();
        saved!.AccessToken.Should().Be("access2");
        saved.RefreshToken.Should().Be("refresh2");                 // rotated token persisted
        saved.SeedFingerprint.Should().Be(Fingerprint(Seed));       // preserved across refresh
        saved.ETag.Should().Be("etag-1");                           // carried for IfMatch
        handler.SentBearers.Should().OnlyContain(b => b == "access2");
    }

    [Fact]
    public async Task ConcurrentCallers_RefreshOnlyOnce()
    {
        var store = new Mock<IFitbitTokenStore>();
        store.Setup(s => s.GetAsync()).ReturnsAsync(
            Doc("access1", "refresh1", DateTimeOffset.UtcNow.AddMinutes(1), Fingerprint(Seed)));
        store.Setup(s => s.SaveAsync(It.IsAny<OAuthToken>())).Returns(Task.CompletedTask);

        var handler = new FakeFitbitHandler
        {
            TokenJson = () => Token("access2", "refresh2"),
            DataJson = Activity(),
            TokenDelay = TimeSpan.FromMilliseconds(50), // widen the window for a refresh race
        };
        var client = CreateClient(handler, store.Object);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetDailyActivityAsync("today")));

        handler.TokenCalls.Should().Be(1);
        store.Verify(s => s.SaveAsync(It.IsAny<OAuthToken>()), Times.Once);
    }

    [Fact]
    public async Task SaveConflict_ReReadsAndReusesWinnersToken()
    {
        var store = new Mock<IFitbitTokenStore>();
        store.SetupSequence(s => s.GetAsync())
            .ReturnsAsync(Doc("access1", "refresh1", DateTimeOffset.UtcNow.AddMinutes(1), Fingerprint(Seed), etag: "etag-A"))
            .ReturnsAsync(FreshDoc("access3"));   // the winner's rotated token, re-read after the 412
        store.Setup(s => s.SaveAsync(It.IsAny<OAuthToken>()))
            .ThrowsAsync(new CosmosException("precondition failed", HttpStatusCode.PreconditionFailed, 0, "", 0));

        var handler = new FakeFitbitHandler { TokenJson = () => Token("access2", "refresh2"), DataJson = Activity() };
        var client = CreateClient(handler, store.Object);

        await client.GetDailyActivityAsync("today");

        handler.TokenCalls.Should().Be(1);                              // refreshed once, did not retry
        handler.SentBearers.Should().OnlyContain(b => b == "access3");  // used the winner's token
    }

    [Fact]
    public async Task NoDocument_BootstrapsFromSeed()
    {
        var store = new Mock<IFitbitTokenStore>();
        store.Setup(s => s.GetAsync()).ReturnsAsync((OAuthToken?)null);
        OAuthToken? saved = null;
        store.Setup(s => s.SaveAsync(It.IsAny<OAuthToken>())).Callback<OAuthToken>(t => saved = t).Returns(Task.CompletedTask);

        var handler = new FakeFitbitHandler { TokenJson = () => Token("accessB", "refreshB"), DataJson = Activity() };
        var client = CreateClient(handler, store.Object);

        await client.GetDailyActivityAsync("today");

        handler.SentRefreshTokens.Should().ContainSingle().Which.Should().Be(Seed); // exchanged the seed
        saved.Should().NotBeNull();
        saved!.SeedFingerprint.Should().Be(Fingerprint(Seed));
        saved.ETag.Should().BeNull();                                  // create path — no IfMatch
        handler.SentBearers.Should().OnlyContain(b => b == "accessB");
    }

    [Fact]
    public async Task SeedChanged_ReBootstraps_OverwritingStaleDocument()
    {
        // Document is still fresh, but its seed fingerprint no longer matches the env seed.
        var store = new Mock<IFitbitTokenStore>();
        store.Setup(s => s.GetAsync()).ReturnsAsync(
            Doc("accessOld", "refreshOld", DateTimeOffset.UtcNow.AddHours(5), seedFp: "OLD-FINGERPRINT", etag: "etag-X"));
        OAuthToken? saved = null;
        store.Setup(s => s.SaveAsync(It.IsAny<OAuthToken>())).Callback<OAuthToken>(t => saved = t).Returns(Task.CompletedTask);

        var handler = new FakeFitbitHandler { TokenJson = () => Token("accessNew", "refreshNew"), DataJson = Activity() };
        var client = CreateClient(handler, store.Object);

        await client.GetDailyActivityAsync("today");

        handler.SentRefreshTokens.Should().ContainSingle().Which.Should().Be(Seed); // seed, not refreshOld
        saved!.SeedFingerprint.Should().Be(Fingerprint(Seed));
        saved.ETag.Should().Be("etag-X");                              // conditional overwrite of stale doc
        handler.SentBearers.Should().OnlyContain(b => b == "accessNew");
    }

    [Fact]
    public async Task Unauthorized_ForcesRefresh_AndRetriesOnce()
    {
        var store = new Mock<IFitbitTokenStore>();
        store.Setup(s => s.GetAsync()).ReturnsAsync(FreshDoc("access1")); // fresh, yet revoked server-side
        store.Setup(s => s.SaveAsync(It.IsAny<OAuthToken>())).Returns(Task.CompletedTask);

        var handler = new FakeFitbitHandler
        {
            TokenJson = () => Token("access2", "refresh2"),
            DataJson = Activity(steps: 1),
            DataStatuses = new Queue<HttpStatusCode>([HttpStatusCode.Unauthorized, HttpStatusCode.OK]),
        };
        var client = CreateClient(handler, store.Object);

        var result = await client.GetDailyActivityAsync("today");

        handler.TokenCalls.Should().Be(1);                       // forced one refresh after the 401
        handler.SentBearers.Should().Equal("access1", "access2"); // first fails, retry succeeds
        result.Steps.Should().Be(1);
    }

    [Fact]
    public async Task NotConnected_NoDocumentAndNoSeed_Throws()
    {
        var store = new Mock<IFitbitTokenStore>();
        store.Setup(s => s.GetAsync()).ReturnsAsync((OAuthToken?)null);
        var client = CreateClient(new FakeFitbitHandler(), store.Object, seed: null);

        var act = () => client.GetDailyActivityAsync("today");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not connected*");
    }

    // ---- Response → model mapping ------------------------------------------

    [Fact]
    public async Task MapsDailyActivity_IncludingGoalAndTotalDistance()
    {
        const string json = """
        {"goals":{"steps":10000},
         "summary":{"steps":8421,"caloriesOut":2310,
           "veryActiveMinutes":22,"fairlyActiveMinutes":15,"lightlyActiveMinutes":180,"sedentaryMinutes":600,
           "distances":[{"activity":"total","distance":6.43},{"activity":"tracker","distance":6.1}]}}
        """;
        var client = CreateClient(new FakeFitbitHandler { DataJson = json }, FreshStore());

        var result = await client.GetDailyActivityAsync("2026-06-06");

        result.Date.Should().Be("2026-06-06");
        result.Steps.Should().Be(8421);
        result.StepsGoal.Should().Be(10000);
        result.Distance.Should().Be(6.43);          // the "total" distance, not "tracker"
        result.CaloriesOut.Should().Be(2310);
        result.VeryActiveMinutes.Should().Be(22);
        result.SedentaryMinutes.Should().Be(600);
    }

    [Fact]
    public async Task MapsSleep_WithStages()
    {
        const string json = """
        {"sleep":[{"isMainSleep":true,"efficiency":94}],
         "summary":{"totalMinutesAsleep":418,"totalTimeInBed":445,
           "stages":{"deep":78,"light":230,"rem":90,"wake":20}}}
        """;
        var client = CreateClient(new FakeFitbitHandler { DataJson = json }, FreshStore());

        var result = await client.GetSleepSummaryAsync("2026-06-06");

        result.AsleepMinutes.Should().Be(418);
        result.InBedMinutes.Should().Be(445);
        result.Efficiency.Should().Be(94);
        result.Stages.Should().NotBeNull();
        result.Stages!.DeepMinutes.Should().Be(78);
        result.Stages.RemMinutes.Should().Be(90);
        result.Stages.AwakeMinutes.Should().Be(20);
    }

    [Fact]
    public async Task MapsSleep_WithoutStages_LeavesStagesNull()
    {
        const string json = """
        {"sleep":[{"isMainSleep":true,"efficiency":88}],
         "summary":{"totalMinutesAsleep":300,"totalTimeInBed":330}}
        """;
        var client = CreateClient(new FakeFitbitHandler { DataJson = json }, FreshStore());

        var result = await client.GetSleepSummaryAsync("2026-06-06");

        result.Stages.Should().BeNull();   // classic/short sleep — no stage breakdown
        result.Efficiency.Should().Be(88);
        result.AsleepMinutes.Should().Be(300);
    }

    [Fact]
    public async Task MapsHeartRate_ToleratingMissingRestingRate()
    {
        const string json = """
        {"activities-heart":[
          {"dateTime":"2026-06-05","value":{"restingHeartRate":58,
            "heartRateZones":[{"name":"Fat Burn","min":91,"max":127,"minutes":40},{"name":"Cardio","min":127,"max":154,"minutes":12}]}},
          {"dateTime":"2026-06-06","value":{
            "heartRateZones":[{"name":"Fat Burn","min":91,"max":127,"minutes":0}]}}
        ]}
        """;
        var client = CreateClient(new FakeFitbitHandler { DataJson = json }, FreshStore());

        var result = await client.GetHeartRateAsync("2026-06-05", "2026-06-06");

        result.Should().HaveCount(2);
        result[0].Date.Should().Be("2026-06-05");
        result[0].RestingHeartRate.Should().Be(58);
        result[0].Zones.Should().HaveCount(2);
        result[0].Zones[0].Name.Should().Be("Fat Burn");
        result[0].Zones[0].Minutes.Should().Be(40);
        result[1].RestingHeartRate.Should().BeNull();   // absent for the day → null
        result[1].Zones.Should().ContainSingle();
    }

    // ---- Helpers -----------------------------------------------------------

    private static FitbitClient CreateClient(FakeFitbitHandler handler, IFitbitTokenStore store, string? seed = Seed)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.fitbit.com") };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(FitbitClient.ClientName)).Returns(http);
        var credentials = new FitbitCredentials { ClientId = "cid", ClientSecret = "csecret", SeedRefreshToken = seed };
        return new FitbitClient(factory.Object, store, credentials);
    }

    /// <summary>A store that always returns a fresh, seed-matching document (so no refresh happens).</summary>
    private static IFitbitTokenStore FreshStore()
    {
        var store = new Mock<IFitbitTokenStore>();
        store.Setup(s => s.GetAsync()).ReturnsAsync(FreshDoc("access-fresh"));
        return store.Object;
    }

    private static OAuthToken FreshDoc(string accessToken) =>
        Doc(accessToken, "refresh-fresh", DateTimeOffset.UtcNow.AddHours(5), Fingerprint(Seed));

    private static OAuthToken Doc(
        string accessToken, string refreshToken, DateTimeOffset expiresAt, string? seedFp = null, string? etag = "etag-1") => new()
    {
        Id = "fitbit",
        Provider = "fitbit",
        AccessToken = accessToken,
        RefreshToken = refreshToken,
        ExpiresAt = expiresAt,
        SeedFingerprint = seedFp,
        ETag = etag,
    };

    private static string Token(string accessToken, string refreshToken) =>
        "{\"access_token\":\"" + accessToken + "\",\"refresh_token\":\"" + refreshToken +
        "\",\"expires_in\":28800,\"token_type\":\"Bearer\",\"user_id\":\"u\"}";

    private static string Activity(int steps = 1) => "{\"summary\":{\"steps\":" + steps + "}}";

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>
    /// Routes by path: the OAuth token endpoint records the refresh token it received and returns
    /// <see cref="TokenJson"/>; every other (data) request records its Bearer token and returns
    /// <see cref="DataJson"/> (or the next status from <see cref="DataStatuses"/>).
    /// </summary>
    private sealed class FakeFitbitHandler : HttpMessageHandler
    {
        private int _tokenCalls;
        private readonly object _gate = new();

        public int TokenCalls => Volatile.Read(ref _tokenCalls);
        public List<string> SentRefreshTokens { get; } = [];
        public List<string?> SentBearers { get; } = [];

        public Func<string>? TokenJson { get; set; }
        public string DataJson { get; set; } = "{}";
        public Queue<HttpStatusCode>? DataStatuses { get; set; }
        public TimeSpan TokenDelay { get; set; } = TimeSpan.Zero;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth2/token", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _tokenCalls);
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                lock (_gate) SentRefreshTokens.Add(FormValue(body, "refresh_token") ?? "");
                if (TokenDelay > TimeSpan.Zero) await Task.Delay(TokenDelay, cancellationToken);
                var json = TokenJson?.Invoke() ?? throw new InvalidOperationException("TokenJson not configured.");
                return JsonResponse(json);
            }

            HttpStatusCode status;
            lock (_gate)
            {
                SentBearers.Add(request.Headers.Authorization?.Parameter);
                status = DataStatuses is { Count: > 0 } ? DataStatuses.Dequeue() : HttpStatusCode.OK;
            }
            return status == HttpStatusCode.OK ? JsonResponse(DataJson) : new HttpResponseMessage(status);
        }

        private static HttpResponseMessage JsonResponse(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static string? FormValue(string body, string key) => body
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(kv => kv.Length == 2 && kv[0] == key)
            .Select(kv => Uri.UnescapeDataString(kv[1]))
            .FirstOrDefault();
    }
}
