using System.Net;
using System.Text;
using System.Text.Json;
using Ancela.Agent.SemanticKernel.Plugins.UnifiPlugin;
using FluentAssertions;
using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// Unit tests for the UniFi client: the exact Connector paths it requests, the console-by-MAC
/// match, the JSON-to-record mapping, and the deliberate omissions (no client list, no ids in
/// error messages). HTTP is faked with a routing stub handler — no live api.ui.com.
///
/// The path assertions exist because the route is easy to "correct" wrongly: the Connector
/// accepts the console-scoped Network path both with and without a `/proxy` segment, and the
/// form below is the one running in production. A reviewer suggesting `/proxy` is not reporting
/// a break; these tests pin what is known to work.
/// </summary>
public class UnifiClientTests
{
    private const string ConsoleId = "900A6F000000";
    private const string SiteId = "4f1e2d3c-0000-4a00-8000-abcdef000001";
    private const string GatewayId = "7a2b3c4d-0000-4b00-8000-abcdef000002";

    // Site Manager reports a bare, upper-case MAC; the Network API colon-separates and lower-cases
    // the same address. Both appear here so the normalisation is actually exercised.
    private const string ConsoleMacBare = "900A6F112233";
    private const string GatewayMacColoned = "90:0a:6f:11:22:33";

    private static string SiteBase => $"/v1/connector/consoles/{ConsoleId}/network/integration/v1/sites/{SiteId}";

    [Fact]
    public async Task GetClientCount_RequestsConsoleScopedPath_WithoutProxySegment()
    {
        var (client, handler) = Build();

        await client.GetClientCountAsync();

        handler.Paths.Should().Equal(
            "/v1/hosts",
            $"/v1/connector/consoles/{ConsoleId}/network/integration/v1/sites",
            $"{SiteBase}/clients");

        handler.Paths.Should().NotContain(p => p.Contains("/proxy/"),
            because: "the deployed integration reaches the Network API without a /proxy segment");
    }

    [Fact]
    public async Task GetClientCount_ReturnsTotalCount_AndNeverFetchesTheClientList()
    {
        var (client, handler) = Build(clientTotalCount: 37);

        var count = await client.GetClientCountAsync();

        count.Should().Be(37);
        handler.Queries.Should().Contain("?limit=1",
            because: "only the total is wanted — household device names, MACs and IPs must stay out");
    }

    [Fact]
    public async Task GetGatewayUptime_MatchesTheConsoleByMac_IgnoringSeparatorsAndCase()
    {
        // A decoy switch is listed first, so a match by position rather than MAC would pick it.
        var (client, _) = Build(extraDeviceMac: "aa:bb:cc:dd:ee:ff");

        var uptime = await client.GetGatewayUptimeAsync();

        uptime.Name.Should().Be("Dream Machine");
        uptime.Model.Should().Be("UDMPROSE");
        uptime.State.Should().Be("ONLINE");
    }

    [Fact]
    public async Task GetGatewayUptime_RequestsStatisticsForTheMatchedDevice()
    {
        var (client, handler) = Build();

        await client.GetGatewayUptimeAsync();

        handler.Paths.Should().Equal(
            "/v1/hosts",
            $"/v1/connector/consoles/{ConsoleId}/network/integration/v1/sites",
            $"{SiteBase}/devices",
            $"{SiteBase}/devices/{GatewayId}/statistics/latest");
    }

    [Theory]
    [InlineData(1016100, 11, "11d 18h 15m")]   // over a day: days surface
    [InlineData(45296, 0, "12h 34m")]          // under a day: hours only
    public async Task GetGatewayUptime_FormatsUptime(long uptimeSec, int expectedDays, string expected)
    {
        var (client, _) = Build(uptimeSec: uptimeSec);

        var uptime = await client.GetGatewayUptimeAsync();

        uptime.UptimeSec.Should().Be(uptimeSec);
        uptime.Uptime.Should().Be(expected);
        if (expectedDays > 0)
            uptime.Uptime.Should().StartWith($"{expectedDays}d");
    }

    [Fact]
    public async Task GetGatewayUptime_WhenStatisticsOmitUptime_ReportsNullWithoutThrowing()
    {
        var (client, _) = Build(uptimeSec: null);

        var uptime = await client.GetGatewayUptimeAsync();

        uptime.UptimeSec.Should().BeNull();
        uptime.Uptime.Should().BeNull();
    }

    [Fact]
    public async Task ConsoleAndSite_AreResolvedOnce_AndReusedAcrossCalls()
    {
        var (client, handler) = Build();

        await client.GetClientCountAsync();
        await client.GetClientCountAsync();

        handler.Paths.Count(p => p == "/v1/hosts").Should().Be(1);
        handler.Paths.Count(p => p.EndsWith("/sites", StringComparison.Ordinal)).Should().Be(1);
        handler.Paths.Count(p => p.EndsWith("/clients", StringComparison.Ordinal)).Should().Be(2);
    }

    [Fact]
    public async Task WhenNoOwnedConsoleIsVisible_Throws()
    {
        var (client, _) = Build(consoleOwned: false);

        var act = async () => await client.GetClientCountAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("No UniFi console owned by this API key");
    }

    [Fact]
    public async Task WhenNoDeviceMatchesTheConsoleMac_Throws()
    {
        var (client, _) = Build(gatewayMac: "11:22:33:44:55:66");

        var act = async () => await client.GetGatewayUptimeAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("No adopted device matches the console's MAC");
    }

    [Fact]
    public async Task RequestFailure_NamesTheEndpoint_ButLeaksNoConsoleOrSiteId()
    {
        // These messages reach the audit log and can be relayed to the owner over SMS, and the
        // path carries the console id (derived from its MAC) and the site id.
        var (client, _) = Build(clientsStatus: HttpStatusCode.Forbidden, clientsBody: "denied");

        var act = async () => await client.GetClientCountAsync();

        var message = (await act.Should().ThrowAsync<HttpRequestException>()).Which.Message;
        message.Should().Contain("client list").And.Contain("403");
        message.Should().NotContain(ConsoleId);
        message.Should().NotContain(SiteId);
    }

    private static (UnifiClient Client, FakeHandler Handler) Build(
        long clientTotalCount = 5,
        long? uptimeSec = 1016100,
        bool consoleOwned = true,
        string gatewayMac = GatewayMacColoned,
        string? extraDeviceMac = null,
        HttpStatusCode clientsStatus = HttpStatusCode.OK,
        string? clientsBody = null)
    {
        // Payloads are serialized from anonymous objects rather than written as JSON literals:
        // the real bodies nest enough braces to fight raw-string interpolation.
        object[] devices = extraDeviceMac is null
            ? [Device(GatewayId, "Dream Machine", "UDMPROSE", gatewayMac)]
            : [
                // A decoy listed first, so a match by position rather than MAC would pick it.
                Device("11112222-0000-4c00-8000-abcdef000003", "Office Switch", "USW24", extraDeviceMac),
                Device(GatewayId, "Dream Machine", "UDMPROSE", gatewayMac),
              ];

        var handler = new FakeHandler
        {
            HostsJson = Json(new
            {
                data = new[]
                {
                    new { id = ConsoleId, type = "console", owner = consoleOwned, reportedState = new { mac = ConsoleMacBare } },
                },
            }),
            SitesJson = Json(new { data = new[] { new { id = SiteId, name = "Default" } } }),
            ClientsJson = Json(new { totalCount = clientTotalCount }),
            ClientsStatus = clientsStatus,
            ClientsBody = clientsBody,
            DevicesJson = Json(new { data = devices }),
            StatisticsJson = uptimeSec is null ? "{}" : Json(new { uptimeSec }),
        };

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.ui.com") };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(UnifiClient.HttpClientName)).Returns(http);

        return (new UnifiClient(factory.Object), handler);
    }

    private static object Device(string id, string name, string model, string macAddress) =>
        new { id, name, model, state = "ONLINE", macAddress };

    private static string Json(object value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>Routes by path and records what was asked for, so tests can pin exact URLs.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<string> Queries { get; } = [];

        public string HostsJson { get; set; } = "{}";
        public string SitesJson { get; set; } = "{}";
        public string ClientsJson { get; set; } = "{}";
        public string DevicesJson { get; set; } = "{}";
        public string StatisticsJson { get; set; } = "{}";
        public HttpStatusCode ClientsStatus { get; set; } = HttpStatusCode.OK;
        public string? ClientsBody { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            Queries.Add(request.RequestUri.Query);

            if (path == "/v1/hosts") return Task.FromResult(Respond(HostsJson));
            if (path.EndsWith("/statistics/latest", StringComparison.Ordinal)) return Task.FromResult(Respond(StatisticsJson));
            if (path.EndsWith("/clients", StringComparison.Ordinal))
                return Task.FromResult(ClientsStatus == HttpStatusCode.OK
                    ? Respond(ClientsJson)
                    : Respond(ClientsBody ?? "", ClientsStatus));
            if (path.EndsWith("/devices", StringComparison.Ordinal)) return Task.FromResult(Respond(DevicesJson));
            if (path.EndsWith("/sites", StringComparison.Ordinal)) return Task.FromResult(Respond(SitesJson));

            return Task.FromResult(Respond("""{"message":"unrouted"}""", HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Respond(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
