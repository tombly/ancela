using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Ancela.Agent.SemanticKernel.Plugins.IpLookupPlugin.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Ancela.Agent.SemanticKernel.Plugins.IpLookupPlugin;

public interface IRdapClient
{
    Task<IpRegistration?> LookupAsync(string ipAddress);
}

/// <summary>
/// Looks IP addresses up in the internet registries over RDAP (RFC 9083), whois's JSON successor:
/// free, no key, and the registries' own record of who holds an address block.
///
/// Every lookup starts at ARIN, which answers for its own addresses and redirects the rest to the
/// registry that holds them: RIPE, APNIC, LACNIC or AFRINIC, and on from LACNIC to Brazil's
/// registro.br (checked 2026-10-26). So no table of address ranges is kept here.
///
/// This is a registry lookup, not a reverse DNS lookup of the address's host name, because a PTR
/// query goes to the DNS server of whoever holds the address. After the model has read email, the
/// addresses it chooses to look up would be a way to signal what it read to a server an outsider
/// runs, which is why web_fetch is switched off after mail. A registry lookup reaches only the
/// registries, so it can stay on.
/// </summary>
public class RdapClient(IHttpClientFactory _httpClientFactory) : IRdapClient
{
    public const string HttpClientName = "rdap";

    /// <summary>The longest name or description line kept from a record. Registry text is written by whoever holds the block.</summary>
    private const int TextCap = 200;

    /// <summary>Registers the HttpClient the lookups use. IpLookupLiveTests registers it the same way.</summary>
    public static IHttpClientBuilder AddHttpClient(IServiceCollection services) =>
        services.AddHttpClient(HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://rdap.arin.net/registry/");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/rdap+json");
        })
        // IPv4 only. LACNIC's IPv6 address didn't answer (2026-10-26), and .NET tries a host's
        // addresses one at a time, so a lookup redirected there hung until the 100-second timeout.
        // Every registry has an IPv4 address.
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new DnsEndPoint(context.DnsEndPoint.Host, context.DnsEndPoint.Port, AddressFamily.InterNetwork), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        });

    public async Task<IpRegistration?> LookupAsync(string ipAddress)
    {
        // Only the address goes into the URL, as IPAddress writes it out (without a %scope), so
        // nothing else the model passes reaches the registry.
        if (!IPAddress.TryParse(ipAddress?.Trim(), out var parsed))
            throw new ArgumentException($"'{ipAddress}' is not an IP address. Pass one like 203.0.113.7 or 2001:db8::1, not a host name.", nameof(ipAddress));
        var address = new IPAddress(parsed.GetAddressBytes()).ToString();

        using var response = await _httpClientFactory.CreateClient(HttpClientName).GetAsync($"ip/{address}");
        // No registry holds the address. The registries answer even for private and reserved
        // addresses (with IANA's record), so this is rare.
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"Registry lookup failed with {(int)response.StatusCode}: {(body.Length > 300 ? body[..300] : body)}",
                null, response.StatusCode);
        }

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return ToRegistration(address, json.RootElement);
    }

    /// <summary>Maps an RDAP ip network object (RFC 9083 section 5.4).</summary>
    public static IpRegistration ToRegistration(string address, JsonElement network)
    {
        var start = Text(network, "startAddress");
        var end = Text(network, "endAddress");
        return new IpRegistration(
            address,
            Text(network, "name"),
            start is not null && end is not null ? $"{start} - {end}" : null,
            Text(network, "country")?.ToUpperInvariant(),
            Description(network),
            Contacts(network));
    }

    // RIPE puts the holder's description in untitled remarks and APNIC in ones titled
    // "description". Other titles are left out: ARIN's "Registration Comments" can be anything
    // (Mailchimp's block carries a whole certificate).
    private static string[] Description(JsonElement network) =>
        Items(network, "remarks")
            .Where(remark => Text(remark, "title") is null or "description")
            .SelectMany(remark => Items(remark, "description"))
            .Where(line => line.ValueKind == JsonValueKind.String)
            .Select(line => Clip(line.GetString()!))
            .Where(line => line.Length > 0)
            .Take(3)
            .ToArray();

    // Only the block's own contacts, not the people listed under them. A name listed twice, as
    // RIPE does, keeps all its roles.
    private static RegistryContact[] Contacts(JsonElement network) =>
        Items(network, "entities")
            .Select(entity => (Name: FullName(entity), Roles: Items(entity, "roles")
                .Where(role => role.ValueKind == JsonValueKind.String)
                .Select(role => role.GetString()!)))
            .Where(contact => contact.Name is not null)
            .GroupBy(contact => contact.Name!)
            .Select(group => new RegistryContact(group.Key, group.SelectMany(c => c.Roles).Distinct().ToArray()))
            .Take(6)
            .ToArray();

    // jCard (RFC 7095): ["vcard", [["version", {}, "text", "4.0"], ["fn", {}, "text", "Google LLC"], ...]]
    private static string? FullName(JsonElement entity)
    {
        if (!entity.TryGetProperty("vcardArray", out var vcard) || vcard.ValueKind != JsonValueKind.Array
            || vcard.GetArrayLength() < 2 || vcard[1].ValueKind != JsonValueKind.Array)
            return null;
        var fn = vcard[1].EnumerateArray().FirstOrDefault(p => p.ValueKind == JsonValueKind.Array
            && p.GetArrayLength() >= 4 && p[0].ValueKind == JsonValueKind.String && p[0].GetString() == "fn");
        return fn.ValueKind == JsonValueKind.Array && fn[3].ValueKind == JsonValueKind.String
            && Clip(fn[3].GetString()!) is { Length: > 0 } name ? name : null;
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && Clip(value.GetString()!) is { Length: > 0 } text ? text : null;

    private static IEnumerable<JsonElement> Items(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

    private static string Clip(string text)
    {
        text = text.Trim();
        return text.Length > TextCap ? text[..TextCap] : text;
    }
}
