using Ancela.Agent.SemanticKernel.Plugins.IpLookupPlugin;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Ancela.Agent.Tests;

/// <summary>
/// Live diagnostic against the real internet registries. Excluded from normal runs (Integration
/// trait) and, like WeatherLiveTests, needs no API key:
///   dotnet test --filter "FullyQualifiedName~IpLookupLiveTests"
/// Every lookup starts at ARIN, so the addresses held elsewhere pin that ARIN still redirects to
/// the registry that holds them, through the HttpClient DependencyModule registers. The LACNIC case
/// also pins the IPv4-only connection: LACNIC's IPv6 address didn't answer.
/// </summary>
[Trait("Category", "Integration")]
public class IpLookupLiveTests(ITestOutputHelper _output)
{
    [Theory]
    [InlineData("209.85.220.41", "Google LLC", null)]                  // ARIN's own
    [InlineData("193.0.6.139", "RIPE NCC", "NL")]                        // redirected to RIPE
    [InlineData("1.1.1.1", "APNIC", "AU")]                               // redirected to APNIC
    [InlineData("200.160.2.3", "NIC.BR", "BR")]                          // to LACNIC, then registro.br
    [InlineData("2a00:1450:4001:80b::200e", "Google Ireland", "IE")]     // IPv6, at RIPE
    public async Task LooksUpAnAddress_AtTheRegistryThatHoldsIt(string address, string holder, string? country)
    {
        var registration = await BuildClient().LookupAsync(address);

        registration.Should().NotBeNull();
        _output.WriteLine($"{address}: {registration!.Network} ({registration.Range}) {registration.Country}");
        foreach (var contact in registration.Contacts)
            _output.WriteLine($"  {contact.Name} [{string.Join(", ", contact.Roles)}]");
        foreach (var line in registration.Description)
            _output.WriteLine($"  \"{line}\"");

        registration.Contacts.Select(c => c.Name).Concat(registration.Description)
            .Should().Contain(text => text.Contains(holder), because: $"{holder} holds {address}");
        if (country is not null)
            registration.Country.Should().Be(country);
    }

    private static IRdapClient BuildClient()
    {
        var services = new ServiceCollection();
        RdapClient.AddHttpClient(services);
        return new RdapClient(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>());
    }
}
