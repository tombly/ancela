using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

/// <summary>
/// One of iCloud's WebDAV servers: the calendar (CalDAV, caldav.icloud.com) or the contacts (CardDAV,
/// contacts.icloud.com), signed in with the mail's iCloud Mail address and app-specific password
/// (ICLOUD_USERNAME, ICLOUD_APP_PASSWORD; both servers took them, probed 2026-11-11). Each names the
/// account's own host and home collection on first use (current-user-principal, then the home set),
/// which is kept: it doesn't change.
/// </summary>
public sealed class ICloudDav : IDisposable
{
    public static readonly XNamespace Dav = "DAV:";
    public static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";
    public static readonly XNamespace CardDav = "urn:ietf:params:xml:ns:carddav";

    private readonly Uri _root;
    private readonly XName _homeSet;
    private readonly string _what;
    private readonly HttpClient _http;
    private Uri? _home;

    /// <param name="what">"calendar" or "contacts", for the errors the model reads.</param>
    public ICloudDav(Uri root, XName homeSet, string what)
    {
        _root = root;
        _homeSet = homeSet;
        _what = what;
        var user = Environment.GetEnvironmentVariable("ICLOUD_USERNAME") ?? throw new InvalidOperationException("ICLOUD_USERNAME not set");
        var password = Environment.GetEnvironmentVariable("ICLOUD_APP_PASSWORD") ?? throw new InvalidOperationException("ICLOUD_APP_PASSWORD not set");
        // A redirect isn't followed: HttpClient would resend a PROPFIND or REPORT without its sign-in,
        // and iCloud answered every request without one (2026-11-11).
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
    }

    /// <summary>The account's home collection: its calendars, or its address books, are the members.</summary>
    public async Task<Uri> HomeAsync()
    {
        if (_home is not null)
            return _home;

        var principal = Href(await SendAsync("PROPFIND", _root, 0, Propfind(Dav + "current-user-principal")), Dav + "current-user-principal", _root);
        _home = Href(await SendAsync("PROPFIND", principal, 0, Propfind(_homeSet)), _homeSet, principal);
        return _home;
    }

    /// <summary>A PROPFIND or REPORT, whose answer is a multistatus; anything else is a failure.</summary>
    public async Task<XDocument> SendAsync(string method, Uri url, int depth, XElement body)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url)
        {
            Content = new StringContent(body.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "application/xml"),
        };
        request.Headers.Add("Depth", depth.ToString());
        using var response = await _http.SendAsync(request);
        Check(response, method);
        if (response.StatusCode != HttpStatusCode.MultiStatus)
            throw new InvalidOperationException($"iCloud's {_what} server answered {method} with {(int)response.StatusCode}, not a list.");
        return XDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    /// <summary>Puts a new resource at <paramref name="url"/>, refusing to replace one already there.</summary>
    public async Task PutNewAsync(Uri url, string contentType, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        using var response = await _http.SendAsync(request);
        Check(response, "PUT");
    }

    /// <summary>The responses in a multistatus, each with its href made absolute.</summary>
    public static IEnumerable<(Uri Href, XElement Response)> Responses(XDocument multistatus, Uri baseUrl) =>
        multistatus.Root?.Elements(Dav + "response")
            .Select(r => (new Uri(baseUrl, r.Element(Dav + "href")?.Value ?? string.Empty), r))
        ?? throw new InvalidOperationException("iCloud answered with an empty list.");

    /// <summary>A PROPFIND body asking for these properties.</summary>
    public static XElement Propfind(params XName[] properties) =>
        new(Dav + "propfind", new XAttribute(XNamespace.Xmlns + "d", Dav.NamespaceName), new XElement(Dav + "prop", properties.Select(p => new XElement(p))));

    private void Check(HttpResponseMessage response, string method)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException($"iCloud refused the sign-in to the {_what}: the app-specific password may have been revoked (changing the Apple Account password revokes them).");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"iCloud's {_what} server answered {method} with {(int)response.StatusCode} {response.ReasonPhrase}.");
    }

    private Uri Href(XDocument multistatus, XName property, Uri baseUrl) =>
        multistatus.Descendants(property).Elements(Dav + "href").FirstOrDefault()?.Value is { Length: > 0 } href
            ? new Uri(baseUrl, href)
            : throw new InvalidOperationException($"iCloud's {_what} server didn't say where the account's {_what} is.");

    public void Dispose() => _http.Dispose();
}
