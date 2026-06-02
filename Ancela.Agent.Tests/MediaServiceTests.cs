using System.Net;
using Ancela.Agent.Services;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// Unit tests for the inbound media fetch/persist guards. No live OpenAI, Twilio, or storage:
/// HTTP is faked with a stub handler and blob storage is mocked.
/// </summary>
public class MediaServiceTests
{
    private const string TwilioUrl = "https://api.twilio.com/2010-04-01/Accounts/AC/Messages/MM/Media/ME";

    [Theory]
    [InlineData("image/jpeg", true)]
    [InlineData("image/png", true)]
    [InlineData("image/gif", true)]
    [InlineData("image/webp", true)]
    [InlineData("image/png; charset=binary", true)] // parameters are ignored
    [InlineData("IMAGE/PNG", true)]                  // case-insensitive
    [InlineData("application/pdf", false)]
    [InlineData("video/mp4", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSupportedImage_AllowsOnlyImages(string? contentType, bool expected)
    {
        var service = CreateService(out _, out _);
        service.IsSupportedImage(contentType).Should().Be(expected);
    }

    [Fact]
    public async Task FetchAsync_NonImageContentType_ReturnsNull_WithoutFetching()
    {
        var service = CreateService(out var httpFactory, out _);

        var result = await service.FetchAsync(TwilioUrl, "application/pdf");

        result.Should().BeNull();
        httpFactory.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("https://evil.example.com/some/path")]              // wrong host
    [InlineData("http://api.twilio.com/Media/ME")]                  // not https
    [InlineData("https://api.twilio.com.evil.com/Media/ME")]        // look-alike host
    [InlineData("not-a-url")]
    public async Task FetchAsync_DisallowedHost_ReturnsNull_WithoutFetching(string url)
    {
        var service = CreateService(out var httpFactory, out _);

        var result = await service.FetchAsync(url, "image/png");

        result.Should().BeNull();
        httpFactory.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task FetchAsync_OversizedPayload_ReturnsNull()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3]),
        };
        // Force a declared size over the cap; FetchAsync should reject before reading the body.
        response.Content.Headers.ContentLength = MediaService.MaxMediaBytes + 1;

        var service = CreateService(out _, out _, response);

        var result = await service.FetchAsync(TwilioUrl, "image/png");

        result.Should().BeNull();
    }

    [Fact]
    public async Task FetchAsync_ValidImage_ReturnsBytesAndNormalizedType()
    {
        byte[] payload = [10, 20, 30, 40];
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };

        var service = CreateService(out _, out _, response);

        var result = await service.FetchAsync(TwilioUrl, "IMAGE/PNG; charset=binary");

        result.Should().NotBeNull();
        result!.Bytes.Should().Equal(payload);
        result.ContentType.Should().Be("image/png");
    }

    [Fact]
    public async Task FetchAsync_HttpError_ReturnsNull()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var service = CreateService(out _, out _, response);

        var result = await service.FetchAsync(TwilioUrl, "image/png");

        result.Should().BeNull();
    }

    [Fact]
    public async Task PersistAsync_UploadsToBlob_AndReturnsUri()
    {
        var blobUri = new Uri("https://acct.blob.core.windows.net/media/inbound/1/2/abc.png");

        var blobClient = new Mock<BlobClient>();
        blobClient
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response<BlobContentInfo>>());
        blobClient.SetupGet(b => b.Uri).Returns(blobUri);

        var container = new Mock<BlobContainerClient>();
        container
            .Setup(c => c.CreateIfNotExistsAsync(
                It.IsAny<PublicAccessType>(),
                It.IsAny<IDictionary<string, string>>(),
                It.IsAny<BlobContainerEncryptionScopeOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response<BlobContainerInfo>>());
        container.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(blobClient.Object);

        var blobService = new Mock<BlobServiceClient>();
        blobService.Setup(s => s.GetBlobContainerClient(It.IsAny<string>())).Returns(container.Object);

        var service = new MediaService(
            Mock.Of<IHttpClientFactory>(),
            blobService.Object,
            NullLogger<MediaService>.Instance);

        var uri = await service.PersistAsync(new MediaItem([1, 2, 3], "image/png"), "+15551234567", "+15559876543");

        uri.Should().Be(blobUri);
        blobClient.Verify(
            b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static MediaService CreateService(
        out Mock<IHttpClientFactory> httpFactory,
        out Mock<BlobServiceClient> blobService,
        HttpResponseMessage? response = null)
    {
        httpFactory = new Mock<IHttpClientFactory>();
        if (response is not null)
        {
            var client = new HttpClient(new StubHttpMessageHandler(response));
            httpFactory.Setup(f => f.CreateClient(MediaService.TwilioMediaClientName)).Returns(client);
        }

        blobService = new Mock<BlobServiceClient>();
        return new MediaService(httpFactory.Object, blobService.Object, NullLogger<MediaService>.Instance);
    }

    private sealed class StubHttpMessageHandler(HttpResponseMessage _response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_response);
    }
}
