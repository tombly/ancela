using System.Net;
using System.Text;
using System.Text.Json;
using Ancela.Agent.SemanticKernel.Plugins.IpLookupPlugin;
using FluentAssertions;
using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// Unit tests for lookup_ip: how each registry's RDAP record becomes what the model reads, and what
/// reaches the registry. The records are real responses (fetched 2026-10-26), cut down.
/// IpLookupLiveTests covers the redirects between registries.
/// </summary>
public class IpLookupTests
{
    // ARIN names the holder as the registrant. The people under it are left out.
    private const string ArinGoogle = """
        {"objectClassName":"ip network","startAddress":"209.85.128.0","endAddress":"209.85.255.255","name":"GOOGLE",
         "entities":[{"vcardArray":["vcard",[["version",{},"text","4.0"],["fn",{},"text","Google LLC"],["kind",{},"text","org"]]],
           "roles":["registrant"],
           "entities":[{"vcardArray":["vcard",[["version",{},"text","4.0"],["fn",{},"text","Abuse"]]],"roles":["abuse"]}]}]}
        """;

    // APNIC often names no registrant; the holder is in the description.
    private const string ApnicChinanet = """
        {"objectClassName":"ip network","startAddress":"61.160.0.0","endAddress":"61.160.255.255","name":"CHINANET-JS","country":"CN",
         "remarks":[{"title":"description","description":["CHINANET jiangsu province network","China Telecom","A12,Xin-Jie-Kou-Wai Street","Nanjing"]}],
         "entities":[
           {"vcardArray":["vcard",[["version",{},"text","4.0"],["fn",{},"text","Chinanet Hostmaster"]]],"roles":["administrative"]},
           {"vcardArray":["vcard",[["version",{},"text","4.0"],["fn",{},"text","CHINANET JIANGSU"]]],"roles":["technical"]}]}
        """;

    // RIPE leaves its description untitled and lists a contact once per role.
    private const string RipeNcc = """
        {"objectClassName":"ip network","startAddress":"193.0.0.0","endAddress":"193.0.7.255","name":"RIPE-NCC","country":"nl",
         "remarks":[{"description":["RIPE Network Coordination Centre","Amsterdam, Netherlands"]}],
         "entities":[
           {"vcardArray":["vcard",[["version",{},"text","4.0"],["fn",{},"text","RIPE NCC Operations"]]],"roles":["technical"]},
           {"vcardArray":["vcard",[["version",{},"text","4.0"],["fn",{},"text","Reseaux IP Europeens Network Coordination Centre (RIPE NCC)"]]],"roles":["registrant"]},
           {"vcardArray":["vcard",[["version",{},"text","4.0"],["fn",{},"text","RIPE NCC Operations"]]],"roles":["abuse"]}]}
        """;

    // ARIN's "Registration Comments" can hold anything: Mailchimp's holds a certificate.
    private const string ArinMailchimp = """
        {"objectClassName":"ip network","startAddress":"198.2.128.0","endAddress":"198.2.191.255","name":"RSG-DELIVERY",
         "remarks":[{"title":"Registration Comments","description":["-----BEGIN CERTIFICATE-----MIIDlTCCAn2gAwIBAgIUa7XuvJhfuiBjKYDgZbD54Yrb2icw"]}],
         "entities":[{"vcardArray":["vcard",[["version",{},"text","4.0"],["fn",{},"text","MailChimp"]]],"roles":["registrant"]}]}
        """;

    [Fact]
    public void Arin_NamesTheRegistrant_NotThePeopleUnderIt()
    {
        var registration = RdapClient.ToRegistration("209.85.220.41", Parse(ArinGoogle));

        registration.Address.Should().Be("209.85.220.41");
        registration.Network.Should().Be("GOOGLE");
        registration.Range.Should().Be("209.85.128.0 - 209.85.255.255");
        registration.Country.Should().BeNull(because: "ARIN gives no country on this block");
        registration.Contacts.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Name = "Google LLC", Roles = new[] { "registrant" } });
        registration.Description.Should().BeEmpty();
    }

    [Fact]
    public void Apnic_WithNoRegistrant_KeepsTheDescriptionThatNamesTheHolder()
    {
        var registration = RdapClient.ToRegistration("61.160.224.1", Parse(ApnicChinanet));

        registration.Description.Should().Equal(
            ["CHINANET jiangsu province network", "China Telecom", "A12,Xin-Jie-Kou-Wai Street"],
            because: "the first few lines are kept");
        registration.Contacts.Select(c => c.Name).Should().Equal("Chinanet Hostmaster", "CHINANET JIANGSU");
        registration.Country.Should().Be("CN");
    }

    [Fact]
    public void Ripe_KeepsItsUntitledDescription_AndMergesAContactListedTwice()
    {
        var registration = RdapClient.ToRegistration("193.0.6.139", Parse(RipeNcc));

        registration.Description.Should().Equal("RIPE Network Coordination Centre", "Amsterdam, Netherlands");
        registration.Contacts.Should().HaveCount(2);
        registration.Contacts.Single(c => c.Name == "RIPE NCC Operations").Roles.Should().Equal("technical", "abuse");
        registration.Country.Should().Be("NL", because: "RIPE writes some country codes in lower case");
    }

    [Fact]
    public void RegistrationComments_AreLeftOut()
    {
        var registration = RdapClient.ToRegistration("198.2.128.1", Parse(ArinMailchimp));

        registration.Description.Should().BeEmpty();
        registration.Contacts.Single().Name.Should().Be("MailChimp");
    }

    [Fact]
    public void LongText_IsClipped()
    {
        var json = $$"""{"name":"{{new string('x', 5000)}}","entities":[]}""";

        RdapClient.ToRegistration("203.0.113.7", Parse(json)).Network!.Length.Should().Be(200);
    }

    [Theory]
    [InlineData(" 209.85.220.41 ", "ip/209.85.220.41")]
    [InlineData("2A00:1450:4001:080B:0000:0000:0000:200E", "ip/2a00:1450:4001:80b::200e")]
    [InlineData("fe80::1%3", "ip/fe80::1")]
    public async Task OnlyTheAddress_WrittenOutInFull_ReachesTheRegistry(string input, string path)
    {
        var (client, handler) = Build(ArinGoogle);

        await client.LookupAsync(input);

        handler.Requests.Should().Equal($"https://rdap.arin.net/registry/{path}");
    }

    [Theory]
    [InlineData("mail-sor-f41.google.com")]
    [InlineData("209.85.220.41/24")]
    [InlineData("")]
    public async Task NotAnAddress_IsRefused_BeforeAnyRequest(string input)
    {
        var (client, handler) = Build(ArinGoogle);

        var act = async () => await client.LookupAsync(input);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*not an IP address*");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AddressNoRegistryHolds_IsFoundNothing_NotAFailure()
    {
        var (client, _) = Build("""{"errorCode":404,"title":"Not Found"}""", HttpStatusCode.NotFound);

        (await client.LookupAsync("203.0.113.7")).Should().BeNull();
    }

    [Fact]
    public async Task RegistryError_IsAFailure()
    {
        var (client, _) = Build("""{"errorCode":429,"title":"Too Many Requests"}""", HttpStatusCode.TooManyRequests);

        var act = async () => await client.LookupAsync("203.0.113.7");

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static (RdapClient Client, FakeHandler Handler) Build(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new FakeHandler(body, status);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(RdapClient.HttpClientName))
            .Returns(() => new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://rdap.arin.net/registry/") });
        return (new RdapClient(factory.Object), handler);
    }

    private sealed class FakeHandler(string body, HttpStatusCode status) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/rdap+json"),
            });
        }
    }
}
