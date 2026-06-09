using System.Net;
using System.Security.Cryptography;
using System.Text;
using Ancela.Agent.SemanticKernel.Plugins.GoogleHealthPlugin;
using Ancela.Agent.SemanticKernel.Plugins.GoogleHealthPlugin.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// Unit tests for the Google Health client: the durable-token OAuth lifecycle and the
/// dailyRollUp/reconcile response→model mapping. No live Google or Cosmos — HTTP is faked with a
/// routing stub handler and the token store is mocked.
/// </summary>
public class GoogleHealthClientTests
{
    private const string Seed = "seed-refresh-token";

    // ---- Token lifecycle ---------------------------------------------------

    [Fact]
    public async Task FreshToken_IsReused_WithoutRefreshing()
    {
        var store = new Mock<IGoogleHealthTokenStore>();
        store.Setup(s => s.GetAsync()).ReturnsAsync(FreshDoc("access1"));
        var client = CreateClient(new FakeHandler { RollupDefaultJson = Rollup("steps", "\"countSum\":1") }, store.Object);

        await client.GetDailyActivityAsync("2026-06-06");

        store.Verify(s => s.SaveAsync(It.IsAny<OAuthToken>()), Times.Never);
        store.Verify(s => s.GetAsync(), Times.Once); // one read, shared across the 4 parallel roll-ups
    }

    [Fact]
    public async Task NearExpiry_Refreshes_UsingRefreshTokenGrant_AndPersists()
    {
        var store = new Mock<IGoogleHealthTokenStore>();
        store.Setup(s => s.GetAsync()).ReturnsAsync(
            Doc("access1", "refresh1", DateTimeOffset.UtcNow.AddMinutes(1), Fingerprint(Seed), etag: "etag-1"));
        OAuthToken? saved = null;
        store.Setup(s => s.SaveAsync(It.IsAny<OAuthToken>())).Callback<OAuthToken>(t => saved = t).Returns(Task.CompletedTask);

        var handler = new FakeHandler { TokenJson = () => Token("access2", "refresh2") };
        var client = CreateClient(handler, store.Object);

        await client.GetDailyActivityAsync("2026-06-06");

        handler.TokenCalls.Should().Be(1);
        handler.SentGrantTypes.Should().ContainSingle().Which.Should().Be("refresh_token");
        handler.SentRefreshTokens.Should().ContainSingle().Which.Should().Be("refresh1");
        saved!.AccessToken.Should().Be("access2");
        saved.RefreshToken.Should().Be("refresh2");
        handler.SentBearers.Should().OnlyContain(b => b == "access2");
    }

    [Fact]
    public async Task Refresh_RetainsExistingRefreshToken_WhenResponseOmitsOne()
    {
        // Google typically does NOT return a refresh_token on refresh.
        var store = new Mock<IGoogleHealthTokenStore>();
        store.Setup(s => s.GetAsync()).ReturnsAsync(
            Doc("access1", "refresh1", DateTimeOffset.UtcNow.AddMinutes(1), Fingerprint(Seed)));
        OAuthToken? saved = null;
        store.Setup(s => s.SaveAsync(It.IsAny<OAuthToken>())).Callback<OAuthToken>(t => saved = t).Returns(Task.CompletedTask);

        var client = CreateClient(new FakeHandler { TokenJson = () => TokenNoRefresh("access2") }, store.Object);

        await client.GetDailyActivityAsync("2026-06-06");

        saved!.AccessToken.Should().Be("access2");
        saved.RefreshToken.Should().Be("refresh1"); // retained
    }

    [Fact]
    public async Task PersistFailure_IsBestEffort_StillReturnsUsableToken()
    {
        // bug_008 hardening: a transient store failure must not lose a freshly minted token.
        var store = new Mock<IGoogleHealthTokenStore>();
        store.Setup(s => s.GetAsync()).ReturnsAsync(
            Doc("access1", "refresh1", DateTimeOffset.UtcNow.AddMinutes(1), Fingerprint(Seed)));
        store.Setup(s => s.SaveAsync(It.IsAny<OAuthToken>())).ThrowsAsync(new Exception("Cosmos 503"));

        var handler = new FakeHandler { TokenJson = () => Token("access2", "refresh2") };
        var client = CreateClient(handler, store.Object);

        var act = async () => await client.GetDailyActivityAsync("2026-06-06");

        await act.Should().NotThrowAsync();           // the persist failure is swallowed
        handler.SentBearers.Should().OnlyContain(b => b == "access2"); // refreshed token still used
        handler.TokenCalls.Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentCallers_RefreshOnlyOnce()
    {
        var store = new Mock<IGoogleHealthTokenStore>();
        store.Setup(s => s.GetAsync()).ReturnsAsync(
            Doc("access1", "refresh1", DateTimeOffset.UtcNow.AddMinutes(1), Fingerprint(Seed)));
        store.Setup(s => s.SaveAsync(It.IsAny<OAuthToken>())).Returns(Task.CompletedTask);

        var handler = new FakeHandler { TokenJson = () => Token("access2", "refresh2"), TokenDelay = TimeSpan.FromMilliseconds(50) };
        var client = CreateClient(handler, store.Object);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.GetSleepSummaryAsync("2026-06-06")));

        handler.TokenCalls.Should().Be(1);
    }

    [Fact]
    public async Task NoDocument_BootstrapsFromSeed()
    {
        var store = new Mock<IGoogleHealthTokenStore>();
        store.Setup(s => s.GetAsync()).ReturnsAsync((OAuthToken?)null);
        OAuthToken? saved = null;
        store.Setup(s => s.SaveAsync(It.IsAny<OAuthToken>())).Callback<OAuthToken>(t => saved = t).Returns(Task.CompletedTask);

        var handler = new FakeHandler { TokenJson = () => Token("accessB", "refreshB") };
        var client = CreateClient(handler, store.Object);

        await client.GetDailyActivityAsync("2026-06-06");

        handler.SentRefreshTokens.Should().ContainSingle().Which.Should().Be(Seed); // exchanged the seed
        saved!.SeedFingerprint.Should().Be(Fingerprint(Seed));
        saved.ETag.Should().BeNull();
    }

    [Fact]
    public async Task SeedChanged_ReBootstraps_UsingSeedNotStaleDocument()
    {
        var store = new Mock<IGoogleHealthTokenStore>();
        store.Setup(s => s.GetAsync()).ReturnsAsync(
            Doc("accessOld", "refreshOld", DateTimeOffset.UtcNow.AddHours(5), seedFp: "OLD-FINGERPRINT", etag: "etag-X"));
        OAuthToken? saved = null;
        store.Setup(s => s.SaveAsync(It.IsAny<OAuthToken>())).Callback<OAuthToken>(t => saved = t).Returns(Task.CompletedTask);

        var client = CreateClient(new FakeHandler { TokenJson = () => Token("accessNew", "refreshNew") }, store.Object);

        await client.GetDailyActivityAsync("2026-06-06");

        store.Object.Should().NotBeNull();
        saved!.SeedFingerprint.Should().Be(Fingerprint(Seed));
        saved.ETag.Should().Be("etag-X"); // conditional overwrite of the stale doc
    }

    [Fact]
    public async Task NotConnected_NoDocumentAndNoSeed_Throws()
    {
        var store = new Mock<IGoogleHealthTokenStore>();
        store.Setup(s => s.GetAsync()).ReturnsAsync((OAuthToken?)null);
        var client = CreateClient(new FakeHandler(), store.Object, seed: null);

        var act = () => client.GetDailyActivityAsync("2026-06-06");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not connected*");
    }

    [Fact]
    public async Task ExpiredRefreshToken_InvalidGrant_ThrowsReauthMessageForTheAgentToRelay()
    {
        // A near-expiry doc forces a refresh; Google answers 400 invalid_grant when the refresh token
        // has expired/been revoked. The message must name `ancela health auth` so the agent (which
        // relays the exception text) can tell the owner exactly how to reconnect.
        var store = new Mock<IGoogleHealthTokenStore>();
        store.Setup(s => s.GetAsync()).ReturnsAsync(
            Doc("access1", "refresh1", DateTimeOffset.UtcNow.AddMinutes(1), Fingerprint(Seed)));
        var handler = new FakeHandler
        {
            TokenStatus = HttpStatusCode.BadRequest,
            TokenJson = () => "{\"error\":\"invalid_grant\",\"error_description\":\"Token has been expired or revoked.\"}",
        };
        var client = CreateClient(handler, store.Object);

        var act = () => client.GetDailyActivityAsync("2026-06-06");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ancela health auth*");
    }

    // ---- Mapping -----------------------------------------------------------

    [Fact]
    public async Task MapsDailyActivity_FromParallelRollups()
    {
        var handler = new FakeHandler
        {
            RollupJsonByType =
            {
                ["steps"] = Rollup("steps", "\"countSum\":8421"),
                ["distance"] = Rollup("distance", "\"millimetersSum\":6430000"),
                ["total-calories"] = Rollup("totalCalories", "\"kcalSum\":2310.4"),
                ["active-minutes"] = Rollup("activeMinutes",
                    "\"activeMinutesRollupByActivityLevel\":[{\"activityLevel\":\"LIGHT\",\"activeMinutesSum\":180},{\"activityLevel\":\"MODERATE\",\"activeMinutesSum\":15},{\"activityLevel\":\"VIGOROUS\",\"activeMinutesSum\":22}]"),
            },
        };
        var client = CreateClient(handler, FreshStore());

        var result = await client.GetDailyActivityAsync("2026-06-06");

        result.Steps.Should().Be(8421);
        result.DistanceKm.Should().Be(6.43);
        result.CaloriesOut.Should().Be(2310);
        result.LightActiveMinutes.Should().Be(180);
        result.ModerateActiveMinutes.Should().Be(15);
        result.VigorousActiveMinutes.Should().Be(22);
    }

    [Fact]
    public async Task MapsSleep_WithStages_DeduplicatingStageSummary()
    {
        // stagesSummary lists each stage twice (as the live API does); all numbers are JSON strings.
        const string json = """
        {"dataPoints":[{"sleep":{"type":"STAGES","summary":{
          "minutesInSleepPeriod":"480","minutesAsleep":"398","minutesAwake":"20",
          "stagesSummary":[
            {"type":"DEEP","minutes":"78"},{"type":"LIGHT","minutes":"230"},
            {"type":"REM","minutes":"90"},{"type":"AWAKE","minutes":"20"},
            {"type":"DEEP","minutes":"78"},{"type":"LIGHT","minutes":"230"},
            {"type":"REM","minutes":"90"},{"type":"AWAKE","minutes":"20"}]}}}]}
        """;
        var client = CreateClient(new FakeHandler { ReconcileJson = json }, FreshStore());

        var result = await client.GetSleepSummaryAsync("2026-06-06");

        result.Stages.Should().NotBeNull();
        result.Stages!.DeepMinutes.Should().Be(78);   // first occurrence, not summed
        result.Stages.LightMinutes.Should().Be(230);
        result.Stages.RemMinutes.Should().Be(90);
        result.Stages.AwakeMinutes.Should().Be(20);
        result.AsleepMinutes.Should().Be(398);
        result.InBedMinutes.Should().Be(480);
        result.Efficiency.Should().Be(83);            // round(398 / 480 * 100)
    }

    [Fact]
    public async Task MapsSleep_WithoutStages_LeavesStagesNull()
    {
        const string json = """
        {"dataPoints":[{"sleep":{"type":"classic","summary":{
          "minutesInSleepPeriod":"330","minutesAsleep":"300","minutesAwake":"30"}}}]}
        """;
        var client = CreateClient(new FakeHandler { ReconcileJson = json }, FreshStore());

        var result = await client.GetSleepSummaryAsync("2026-06-06");

        result.Stages.Should().BeNull();
        result.AsleepMinutes.Should().Be(300);
        result.InBedMinutes.Should().Be(330);
        result.Efficiency.Should().Be(91);            // round(300 / 330 * 100)
    }

    [Fact]
    public async Task MapsRestingHeartRate_FromReconciledDailyValues()
    {
        // reconcile returns one point per day; beatsPerMinute is a single JSON-string value, and a
        // day without a computed value omits it. Date is nested under dailyRestingHeartRate.
        const string json = """
        {"dataPoints":[
          {"dailyRestingHeartRate":{"date":{"year":2026,"month":6,"day":6},"beatsPerMinute":"64"}},
          {"dailyRestingHeartRate":{"date":{"year":2026,"month":6,"day":5},"beatsPerMinute":"65"}},
          {"dailyRestingHeartRate":{"date":{"year":2026,"month":6,"day":7}}}
        ]}
        """;
        var client = CreateClient(new FakeHandler { ReconcileJson = json }, FreshStore());

        var result = await client.GetHeartRateAsync("2026-06-05", "2026-06-07");

        result.Should().HaveCount(3);
        result[0].Date.Should().Be("2026-06-05");   // ordered ascending by date
        result[0].RestingHeartRate.Should().Be(65);
        result[1].Date.Should().Be("2026-06-06");
        result[1].RestingHeartRate.Should().Be(64);
        result[2].Date.Should().Be("2026-06-07");
        result[2].RestingHeartRate.Should().BeNull();
    }

    [Fact]
    public async Task HeartRate_RangeOver14Days_Throws()
    {
        var client = CreateClient(new FakeHandler(), FreshStore());

        var act = () => client.GetHeartRateAsync("2026-06-01", "2026-06-20");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*14-day*");
    }

    // ---- Helpers -----------------------------------------------------------

    private static GoogleHealthClient CreateClient(FakeHandler handler, IGoogleHealthTokenStore store, string? seed = Seed)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://health.googleapis.com") };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(GoogleHealthClient.ClientName)).Returns(http);
        var credentials = new GoogleHealthCredentials { ClientId = "cid", ClientSecret = "csecret", SeedRefreshToken = seed };
        return new GoogleHealthClient(factory.Object, store, credentials, NullLogger<GoogleHealthClient>.Instance);
    }

    private static IGoogleHealthTokenStore FreshStore()
    {
        var store = new Mock<IGoogleHealthTokenStore>();
        store.Setup(s => s.GetAsync()).ReturnsAsync(FreshDoc("access-fresh"));
        return store.Object;
    }

    private static OAuthToken FreshDoc(string accessToken) =>
        Doc(accessToken, "refresh-fresh", DateTimeOffset.UtcNow.AddHours(5), Fingerprint(Seed));

    private static OAuthToken Doc(
        string accessToken, string refreshToken, DateTimeOffset expiresAt, string? seedFp = null, string? etag = "etag-1") => new()
    {
        Id = "google-health",
        Provider = "google-health",
        AccessToken = accessToken,
        RefreshToken = refreshToken,
        ExpiresAt = expiresAt,
        SeedFingerprint = seedFp,
        ETag = etag,
    };

    private static string Token(string accessToken, string refreshToken) =>
        "{\"access_token\":\"" + accessToken + "\",\"refresh_token\":\"" + refreshToken +
        "\",\"expires_in\":3599,\"token_type\":\"Bearer\"}";

    private static string TokenNoRefresh(string accessToken) =>
        "{\"access_token\":\"" + accessToken + "\",\"expires_in\":3599,\"token_type\":\"Bearer\"}";

    private static string Rollup(string valueKey, string innerJson) =>
        "{\"rollupDataPoints\":[{\"" + valueKey + "\":{" + innerJson + "}}]}";

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>
    /// Routes by request path: the Google token endpoint (POST .../token) records the grant and
    /// returns <see cref="TokenJson"/>; <c>:reconcile</c> returns <see cref="ReconcileJson"/>; each
    /// <c>:dailyRollUp</c> returns the JSON for its data type (extracted from the path).
    /// </summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private int _tokenCalls;
        private readonly object _gate = new();

        public int TokenCalls => Volatile.Read(ref _tokenCalls);
        public List<string?> SentGrantTypes { get; } = [];
        public List<string?> SentRefreshTokens { get; } = [];
        public List<string?> SentBearers { get; } = [];

        public Func<string>? TokenJson { get; set; }
        public HttpStatusCode TokenStatus { get; set; } = HttpStatusCode.OK;
        public TimeSpan TokenDelay { get; set; } = TimeSpan.Zero;
        public Dictionary<string, string> RollupJsonByType { get; } = [];
        public string RollupDefaultJson { get; set; } = "{}";
        public string ReconcileJson { get; set; } = "{}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/token", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _tokenCalls);
                var form = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                lock (_gate)
                {
                    SentGrantTypes.Add(FormValue(form, "grant_type"));
                    SentRefreshTokens.Add(FormValue(form, "refresh_token"));
                }
                if (TokenDelay > TimeSpan.Zero) await Task.Delay(TokenDelay, cancellationToken);
                return Json(TokenJson?.Invoke() ?? throw new InvalidOperationException("TokenJson not configured."), TokenStatus);
            }

            lock (_gate) SentBearers.Add(request.Headers.Authorization?.Parameter);

            if (path.Contains(":reconcile", StringComparison.Ordinal))
                return Json(ReconcileJson);

            var dataType = DataTypeFromPath(path);
            return Json(RollupJsonByType.TryGetValue(dataType, out var json) ? json : RollupDefaultJson);
        }

        private static string DataTypeFromPath(string path)
        {
            const string marker = "/dataTypes/";
            var start = path.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return "";
            start += marker.Length;
            var end = path.IndexOf("/dataPoints", start, StringComparison.Ordinal);
            return end < 0 ? path[start..] : path[start..end];
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static string? FormValue(string body, string key) => body
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(kv => kv.Length == 2 && kv[0] == key)
            .Select(kv => Uri.UnescapeDataString(kv[1]))
            .FirstOrDefault();
    }
}
