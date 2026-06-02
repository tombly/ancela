using System.Net.Http.Headers;
using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;

namespace Ancela.Agent.Services;

/// <summary>
/// An inbound media attachment as delivered by Twilio: the URL to fetch the bytes from and the
/// content type Twilio reported. Carried through the queue and call chain so the agent can decide
/// image-vs-other without sniffing bytes.
/// </summary>
public record Media(string Url, string ContentType);

/// <summary>The fetched bytes of a media item plus its (normalized) content type.</summary>
public record MediaItem(byte[] Bytes, string ContentType);

public interface IMediaService
{
    /// <summary>True for the image content types Ancela can analyze (jpeg, png, gif, webp).</summary>
    bool IsSupportedImage(string? contentType);

    /// <summary>
    /// Fetches a Twilio media URL over authenticated HTTP. Returns <c>null</c> (never throws) when
    /// the content type isn't a supported image, the host isn't <c>api.twilio.com</c> (SSRF guard),
    /// the payload exceeds the size cap, or the request fails.
    /// </summary>
    Task<MediaItem?> FetchAsync(string mediaUrl, string contentType);

    /// <summary>
    /// Copies bytes into the private 'media' blob container so the image outlives Twilio's media
    /// retention. Best-effort: returns <c>null</c> (and logs) on failure rather than throwing, so a
    /// storage hiccup never blocks answering the user. Blobs are private; outbound sending (Phase 3)
    /// mints a short-lived SAS URL at send time.
    /// </summary>
    Task<Uri?> PersistAsync(MediaItem item, string agentPhoneNumber, string userPhoneNumber);
}

public class MediaService(
    IHttpClientFactory _httpClientFactory,
    BlobServiceClient _blobServiceClient,
    ILogger<MediaService> _logger) : IMediaService
{
    /// <summary>Named <see cref="HttpClient"/> carrying the Twilio HTTP Basic auth header.</summary>
    public const string TwilioMediaClientName = "twilio-media";

    private const string ContainerName = "media";
    private const string AllowedHost = "api.twilio.com";

    // Twilio caps MMS media at ~5 MB; reject anything larger as a cost/abuse guard.
    public const long MaxMediaBytes = 5 * 1024 * 1024;

    // Supported inbound image types mapped to a blob file extension.
    private static readonly Dictionary<string, string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = "jpg",
        ["image/png"] = "png",
        ["image/gif"] = "gif",
        ["image/webp"] = "webp",
    };

    public bool IsSupportedImage(string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType) && ImageExtensions.ContainsKey(Normalize(contentType));

    public async Task<MediaItem?> FetchAsync(string mediaUrl, string contentType)
    {
        if (!IsSupportedImage(contentType))
        {
            _logger.LogWarning("MediaService: refusing non-image content type '{ContentType}'", contentType);
            return null;
        }

        // SSRF guard: only fetch the Twilio media host we were handed. Twilio media URLs redirect
        // to a pre-signed CDN/S3 location on a different host; the HttpClient follows that redirect
        // and SocketsHttpHandler strips the Authorization header cross-origin, which is correct —
        // the redirect target is already pre-signed and must not receive our Twilio credentials.
        if (!Uri.TryCreate(mediaUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals(AllowedHost, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("MediaService: refusing media URL with disallowed host: {MediaUrl}", mediaUrl);
            return null;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(TwilioMediaClientName);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("MediaService: fetch failed with status {Status}", (int)response.StatusCode);
                return null;
            }

            // Fail fast on a declared size over the cap...
            if (response.Content.Headers.ContentLength is long declared && declared > MaxMediaBytes)
            {
                _logger.LogWarning("MediaService: media too large ({Bytes} bytes)", declared);
                return null;
            }

            // ...and still enforce the cap while streaming, so a missing or dishonest Content-Length
            // (e.g. a chunked response) can't force an unbounded read into memory.
            await using var source = await response.Content.ReadAsStreamAsync();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + read > MaxMediaBytes)
                {
                    _logger.LogWarning("MediaService: media exceeded {Max} bytes", MaxMediaBytes);
                    return null;
                }
                buffer.Write(chunk, 0, read);
            }

            return new MediaItem(buffer.ToArray(), Normalize(contentType));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MediaService: error fetching media");
            return null;
        }
    }

    public async Task<Uri?> PersistAsync(MediaItem item, string agentPhoneNumber, string userPhoneNumber)
    {
        try
        {
            var container = _blobServiceClient.GetBlobContainerClient(ContainerName);
            await container.CreateIfNotExistsAsync();

            var extension = ImageExtensions.TryGetValue(Normalize(item.ContentType), out var ext) ? ext : "bin";
            var blobName = $"inbound/{Sanitize(agentPhoneNumber)}/{Sanitize(userPhoneNumber)}/{Guid.NewGuid():N}.{extension}";
            var blob = container.GetBlobClient(blobName);

            using var stream = new MemoryStream(item.Bytes);
            await blob.UploadAsync(stream, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = Normalize(item.ContentType) }
            });

            return blob.Uri;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MediaService: error persisting media to blob storage");
            return null;
        }
    }

    // Strips any parameters (e.g. "; charset=...") and lowercases so comparisons are stable.
    private static string Normalize(string contentType) => contentType.Split(';')[0].Trim().ToLowerInvariant();

    // Blob names can't contain '+'; phone numbers reduce to their digits for a clean path segment.
    private static string Sanitize(string phoneNumber) => new(phoneNumber.Where(char.IsDigit).ToArray());
}
