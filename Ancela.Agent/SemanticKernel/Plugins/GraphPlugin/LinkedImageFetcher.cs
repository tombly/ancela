using System.Net;
using System.Net.Sockets;
using Ancela.Agent.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

public interface ILinkedImageFetcher
{
    /// <summary>
    /// Downloads the images an email links to. Returns the ones that arrived, keyed by address; one
    /// that is refused or fails is simply absent, and the caller counts it as missing.
    /// </summary>
    Task<IReadOnlyDictionary<string, MediaItem>> FetchAsync(IEnumerable<string> urls, CancellationToken cancellationToken = default);
}

/// <summary>
/// Downloads images linked from an email, which only happens once the owner has said yes:
/// downloading one tells its sender the email was opened. The addresses come from whoever sent
/// the email, so only https is fetched, every connection (redirects included) must go to a public
/// address — never the Azure metadata service, the container environment or a private network —
/// and only PNG, JPEG and GIF up to <see cref="MaxImageBytes"/> are kept.
/// </summary>
public class LinkedImageFetcher : ILinkedImageFetcher
{
    public const int MaxImageBytes = 1024 * 1024;
    private const int MaxParallel = 6;
    private static readonly TimeSpan PerImageTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(30);

    // The types Graph's PDF converter is known to show. WebP and SVG are not asked for or kept.
    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/gif" };

    private readonly HttpClient _http;
    private readonly ILogger _logger;

    /// <param name="handler">For tests only; it bypasses the public-address check on connections.</param>
    public LinkedImageFetcher(ILogger<LinkedImageFetcher>? logger = null, HttpMessageHandler? handler = null)
    {
        _logger = logger ?? NullLogger<LinkedImageFetcher>.Instance;
        _http = new HttpClient(handler ?? PublicOnlyHandler()) { Timeout = PerImageTimeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Ancela/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("image/png,image/jpeg,image/gif;q=0.9");
    }

    public async Task<IReadOnlyDictionary<string, MediaItem>> FetchAsync(IEnumerable<string> urls, CancellationToken cancellationToken = default)
    {
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(OverallTimeout);

        var fetched = new System.Collections.Concurrent.ConcurrentDictionary<string, MediaItem>();
        await Parallel.ForEachAsync(urls.Distinct(), new ParallelOptions { MaxDegreeOfParallelism = MaxParallel }, async (url, _) =>
        {
            if (await FetchOneAsync(url, overall.Token) is { } image)
                fetched[url] = image;
        });
        return fetched;
    }

    private async Task<MediaItem?> FetchOneAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return null;

        // Log the host only: the rest of a linked image's address often identifies the recipient.
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var type = response.Content.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode || type is null || !ImageTypes.Contains(type)
                || response.Content.Headers.ContentLength > MaxImageBytes)
            {
                _logger.LogInformation("Linked image from {Host} not used: {Status} {Type}", uri.Host, (int)response.StatusCode, type);
                return null;
            }

            // A missing or wrong Content-Length can't force an unbounded read.
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaxImageBytes)
                {
                    _logger.LogInformation("Linked image from {Host} not used: over {Max} bytes", uri.Host, MaxImageBytes);
                    return null;
                }
                buffer.Write(chunk, 0, read);
            }
            return new MediaItem(buffer.ToArray(), type.ToLowerInvariant());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            _logger.LogInformation("Linked image from {Host} not downloaded: {Error}", uri.Host, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Resolves the host itself and connects only when every address it resolves to is public, so a
    /// name pointing at an internal address (or re-pointed between lookup and connect) is refused.
    /// Every redirect opens its connection here too, and .NET won't follow https to http.
    /// </summary>
    private static SocketsHttpHandler PublicOnlyHandler() => new()
    {
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 3,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
            if (addresses.Length == 0 || !addresses.All(IsPublic))
                throw new HttpRequestException($"{context.DnsEndPoint.Host} does not resolve to a public address.");

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    /// <summary>True for an address on the public internet; false for loopback, private, link-local,
    /// shared, documentation, multicast and reserved ranges, and IPv6 forms that carry an IPv4 one.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        var b = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => !(
                b[0] is 0 or 10 or 127 or >= 224                     // this network, private, loopback, multicast and reserved
                || (b[0] == 100 && (b[1] & 0xC0) == 64)              // 100.64/10, carrier-grade NAT
                || (b[0] == 169 && b[1] == 254)                      // link-local, including the Azure metadata service
                || (b[0] == 172 && (b[1] & 0xF0) == 16)              // 172.16/12
                || (b[0] == 192 && b[1] == 168)                      // 192.168/16
                || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2)      // IETF protocol assignments, documentation
                || (b[0] == 198 && (b[1] & 0xFE) == 18)              // 198.18/15, benchmarking
                || (b[0] == 198 && b[1] == 51 && b[2] == 100)        // documentation
                || (b[0] == 203 && b[1] == 0 && b[2] == 113)),       // documentation
            // Global unicast (2000::/3) only, less documentation, 6to4 and Teredo.
            AddressFamily.InterNetworkV6 =>
                (b[0] & 0xE0) == 0x20
                && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8)
                && !(b[0] == 0x20 && b[1] == 0x02)
                && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00),
            _ => false,
        };
    }
}
