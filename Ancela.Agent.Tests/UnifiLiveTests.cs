using Ancela.Agent.SemanticKernel.Plugins.UnifiPlugin;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Ancela.Agent.Tests;

/// <summary>
/// Live diagnostic against the real UniFi Site Manager / Connector API. Excluded from normal runs
/// (Integration trait) and skips unless UNIFI_API_KEY is set:
///   UNIFI_API_KEY=… dotnet test --filter "FullyQualifiedName~UnifiLiveTests"
/// Exercises the real <see cref="UnifiClient"/> so the JSON-to-record binding is covered, not just
/// the URL shapes — the console MAC, the device MAC, and uptimeSec all have to bind for this to pass.
/// </summary>
[Trait("Category", "Integration")]
public class UnifiLiveTests(ITestOutputHelper _output)
{
    [Fact]
    public async Task ReadsClientCountAndGatewayUptime()
    {
        var apiKey = Environment.GetEnvironmentVariable("UNIFI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _output.WriteLine("Skipped: set UNIFI_API_KEY to run.");
            return;
        }

        var services = new ServiceCollection();
        services.AddHttpClient(UnifiClient.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.ui.com");
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-API-Key", apiKey);
        });
        using var provider = services.BuildServiceProvider();
        var unifi = new UnifiClient(provider.GetRequiredService<IHttpClientFactory>());

        var clientCount = await unifi.GetClientCountAsync();
        var gateway = await unifi.GetGatewayUptimeAsync();

        _output.WriteLine($"clients={clientCount} gateway={gateway.Name} model={gateway.Model} "
            + $"state={gateway.State} uptime={gateway.Uptime} ({gateway.UptimeSec}s)");

        clientCount.Should().BeGreaterThan(0);
        gateway.UptimeSec.Should().NotBeNull();
        gateway.Uptime.Should().NotBeNullOrWhiteSpace();
    }
}
