using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using FluentAssertions;

namespace Ancela.Agent.Tests;

/// <summary>
/// The addresses an email's images link to are written by its sender, so the fetcher downloads only
/// https images from public addresses. A refused or failed image is absent from the result, never an
/// exception, and is counted as missing by the caller.
/// </summary>
public class LinkedImageFetcherTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void IsPublic_AcceptsPublicAddresses(string address)
    {
        LinkedImageFetcher.IsPublic(IPAddress.Parse(address)).Should().BeTrue();
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]  // the Azure metadata service
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("192.0.2.1")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("2001:db8::1")]
    [InlineData("2002:a00:1::1")]      // 6to4 carrying 10.0.0.1
    [InlineData("2001:0:4136:e378::1")] // Teredo
    public void IsPublic_RefusesEverythingElse(string address)
    {
        LinkedImageFetcher.IsPublic(IPAddress.Parse(address)).Should().BeFalse();
    }

    [Fact]
    public async Task Fetch_ReturnsEachImage_ByItsAddress_DownloadingADuplicateOnce()
    {
        var handler = new FakeHandler(_ => Image(Png, "image/png"));

        var images = await new LinkedImageFetcher(handler: handler)
            .FetchAsync(["https://cdn.example.com/a.png", "https://cdn.example.com/a.png"]);

        images.Should().ContainSingle();
        images["https://cdn.example.com/a.png"].Bytes.Should().Equal(Png);
        images["https://cdn.example.com/a.png"].ContentType.Should().Be("image/png");
        handler.Requests.Should().Equal("https://cdn.example.com/a.png");
    }

    [Theory]
    [InlineData("http://cdn.example.com/a.png")]
    [InlineData("ftp://cdn.example.com/a.png")]
    [InlineData("cid:part1")]
    [InlineData("not a url")]
    public async Task Fetch_RefusesAnythingButHttps_WithoutARequest(string url)
    {
        var handler = new FakeHandler(_ => Image(Png, "image/png"));

        var images = await new LinkedImageFetcher(handler: handler).FetchAsync([url]);

        images.Should().BeEmpty();
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    [InlineData("image/webp")]
    [InlineData(null)]
    public async Task Fetch_KeepsOnlyPngJpegAndGif(string? contentType)
    {
        var images = await new LinkedImageFetcher(handler: new FakeHandler(_ => Image(Png, contentType)))
            .FetchAsync(["https://cdn.example.com/a"]);

        images.Should().BeEmpty();
    }

    [Fact]
    public async Task Fetch_RefusesAnImageOverTheCap_DeclaredOrNot()
    {
        var big = new byte[LinkedImageFetcher.MaxImageBytes + 1];
        var declared = new FakeHandler(_ => Image(big, "image/jpeg"));
        var undeclared = new FakeHandler(_ =>
        {
            var response = Image(big, "image/jpeg");
            response.Content = new StreamContent(new MemoryStream(big)); // no Content-Length
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return response;
        });

        (await new LinkedImageFetcher(handler: declared).FetchAsync(["https://cdn.example.com/a.jpg"])).Should().BeEmpty();
        (await new LinkedImageFetcher(handler: undeclared).FetchAsync(["https://cdn.example.com/a.jpg"])).Should().BeEmpty();
    }

    [Fact]
    public async Task Fetch_LeavesOutAFailedImage_AndKeepsTheOthers()
    {
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/gone.png" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "/broken.png" => throw new HttpRequestException("connection reset"),
            _ => Image(Png, "image/gif"),
        });

        var images = await new LinkedImageFetcher(handler: handler)
            .FetchAsync(["https://cdn.example.com/gone.png", "https://cdn.example.com/broken.png", "https://cdn.example.com/ok.gif"]);

        images.Keys.Should().Equal("https://cdn.example.com/ok.gif");
    }

    [Theory]
    [InlineData("https://127.0.0.1:{0}/a.png")]
    [InlineData("https://localhost:{0}/a.png")]
    public async Task Fetch_NeverConnectsToANonPublicAddress(string template)
    {
        // The real handler: a listener on loopback must never see a connection.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var images = await new LinkedImageFetcher().FetchAsync([string.Format(template, port)]);

            images.Should().BeEmpty();
            listener.Pending().Should().BeFalse(because: "the address is refused before any connection is made");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static HttpResponseMessage Image(byte[] bytes, string? contentType)
    {
        var content = new ByteArrayContent(bytes);
        if (contentType is not null)
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
                Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }
}
