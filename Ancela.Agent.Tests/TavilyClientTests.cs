using System.Net;
using System.Text;
using Ancela.Agent.SemanticKernel.Plugins.WebPlugin;
using FluentAssertions;
using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// web_fetch has to tell a page it couldn't read apart from a page with no text. Tavily answers
/// 200 either way and puts an unreadable page under failed_results (seen live on 2026-10-20), which
/// the client used to return as "".
/// </summary>
public class TavilyClientTests
{
    [Fact]
    public async Task Fetch_PageTavilyCouldNotRead_Throws_WithTheReason()
    {
        var client = ClientReturning("""
            {"results":[],"failed_results":[{"url":"https://events.example.com/join/abc","error":"Failed to fetch url"}],"response_time":0.4}
            """);

        var act = () => client.FetchAsync("https://events.example.com/join/abc");

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*Failed to fetch url*");
    }

    [Fact]
    public async Task Fetch_ReadablePage_ReturnsItsText()
    {
        var client = ClientReturning("""
            {"results":[{"url":"https://example.com","raw_content":"Example Domain"}],"failed_results":[]}
            """);

        (await client.FetchAsync("https://example.com")).Should().Be("Example Domain");
    }

    [Fact]
    public async Task Fetch_PageWithNoText_ReturnsEmpty_ForTheFilterToReportAsNothingFound()
    {
        var client = ClientReturning("""
            {"results":[{"url":"https://example.com/app","raw_content":""}],"failed_results":[]}
            """);

        (await client.FetchAsync("https://example.com/app")).Should().BeEmpty();
    }

    private static TavilyClient ClientReturning(string json)
    {
        var http = new HttpClient(new StubHandler(json)) { BaseAddress = new Uri("https://api.tavily.com") };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("tavily")).Returns(http);
        return new TavilyClient(factory.Object);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
