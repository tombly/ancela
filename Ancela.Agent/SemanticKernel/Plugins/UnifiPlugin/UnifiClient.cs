using System.Net.Http.Json;
using Ancela.Agent.SemanticKernel.Plugins.UnifiPlugin.Models;

namespace Ancela.Agent.SemanticKernel.Plugins.UnifiPlugin;

public interface IUnifiClient
{
    Task<long> GetClientCountAsync();
    Task<DeviceUptime> GetGatewayUptimeAsync();
}

/// <summary>
/// Reads the owner's UniFi console through Ubiquiti's cloud Connector (api.ui.com), which proxies
/// requests to the console's local Network Integration API, so nothing at home is exposed.
/// Authenticates with a Site Manager API key (UNIFI_API_KEY) configured on the named HttpClient.
/// </summary>
public class UnifiClient(IHttpClientFactory _httpClientFactory) : IUnifiClient
{
    public const string HttpClientName = "unifi";

    // The console's API base path and MAC don't change, so resolve them once.
    private ConsoleRef? _console;

    public async Task<long> GetClientCountAsync()
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var console = await GetConsoleAsync(client);
        var page = await GetAsync<CountPage>(client, $"{console.SiteBase}/clients?limit=1", "client list");
        return page.TotalCount;
    }

    public async Task<DeviceUptime> GetGatewayUptimeAsync()
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var console = await GetConsoleAsync(client);
        var devices = await GetAsync<Page<Device>>(client, $"{console.SiteBase}/devices?limit=200", "device list");

        // The console is itself the gateway, and it does not advertise a "gateway" feature — a
        // UDM Pro SE reports only "switching" — so match it by MAC rather than feature or model.
        var gateway = devices.Data.FirstOrDefault(d => NormalizeMac(d.MacAddress) == console.Mac)
            ?? throw new InvalidOperationException("No adopted device matches the console's MAC address.");

        var stats = await GetAsync<DeviceStatistics>(client,
            $"{console.SiteBase}/devices/{gateway.Id}/statistics/latest", "device statistics");
        return new DeviceUptime(gateway.Name, gateway.Model, gateway.State, stats.UptimeSec,
            stats.UptimeSec is long seconds ? FormatUptime(seconds) : null);
    }

    private async Task<ConsoleRef> GetConsoleAsync(HttpClient client)
    {
        if (_console is not null)
            return _console;

        var hosts = await GetAsync<Page<Host>>(client, "/v1/hosts", "host list");
        var console = hosts.Data.FirstOrDefault(h => h.Owner && h.Type == "console")
            ?? throw new InvalidOperationException("No UniFi console owned by this API key was found.");
        var mac = NormalizeMac(console.ReportedState?.Mac);
        if (mac.Length == 0)
            throw new InvalidOperationException("The UniFi console did not report a MAC address.");

        var networkBase = $"/v1/connector/consoles/{console.Id}/network/integration/v1";
        var sites = await GetAsync<Page<Site>>(client, $"{networkBase}/sites", "site list");
        var site = sites.Data.FirstOrDefault()
            ?? throw new InvalidOperationException("The UniFi console has no Network sites.");

        return _console = new ConsoleRef($"{networkBase}/sites/{site.Id}", mac);
    }

    /// <summary>Site Manager reports a bare MAC ("AABBCC001122"); the Network API colon-separates
    /// and lower-cases it ("aa:bb:cc:00:11:22"), so both sides are normalised before comparing.</summary>
    private static string NormalizeMac(string? mac) =>
        (mac ?? string.Empty).Replace(":", string.Empty).Replace("-", string.Empty).ToUpperInvariant();

    /// <summary>
    /// Names the <paramref name="endpoint"/> rather than the path in failures: the path carries the
    /// console id (derived from its MAC) and the site id, and these messages reach the audit log and
    /// can be relayed to the owner over SMS.
    /// </summary>
    private static async Task<T> GetAsync<T>(HttpClient client, string path, string endpoint)
    {
        using var response = await client.GetAsync(path);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"UniFi {endpoint} request failed with {(int)response.StatusCode}: {(body.Length > 300 ? body[..300] : body)}",
                null, response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>()
            ?? throw new InvalidOperationException($"UniFi {endpoint} request returned an empty body.");
    }

    private static string FormatUptime(long seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalDays >= 1
            ? $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m"
            : $"{span.Hours}h {span.Minutes}m";
    }

    // Both Site Manager and Network Integration list responses wrap results in `data`; the
    // Network ones add paging fields. ReadFromJsonAsync binds camelCase names case-insensitively.
    private sealed record ConsoleRef(string SiteBase, string Mac);
    private record Page<T>(T[] Data);
    private record CountPage(long TotalCount);
    private record Host(string Id, string? Type, bool Owner, HostState? ReportedState);
    private record HostState(string? Mac);
    private record Site(Guid Id, string? Name);
    private record Device(Guid Id, string Name, string? Model, string? State, string? MacAddress);
    private record DeviceStatistics(long? UptimeSec);
}
