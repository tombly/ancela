using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using Azure.Identity;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Graph;
using Xunit.Abstractions;
using MailFolder = Ancela.Agent.SemanticKernel.Plugins.GraphPlugin.MailFolder;

namespace Ancela.Agent.Tests;

/// <summary>
/// Live diagnostic for get_recent_emails' search against the real mailbox. Excluded from normal
/// runs (Integration trait) and skips unless the Graph app settings (Parameters:graph-* in user
/// secrets, or the GRAPH_* environment variables) are set:
///   dotnet test --filter "FullyQualifiedName~MailSearchLiveTests"
/// Pins the bug the search exists for: an email older than the 50 newest is invisible to a plain
/// listing, so the model saved the wrong one. Prints counts only, never addresses or subjects.
/// </summary>
[Trait("Category", "Integration")]
public class MailSearchLiveTests(ITestOutputHelper _output)
{
    [Fact]
    public async Task Search_FindsAnEmailOlderThanTheNewest50()
    {
        if (Load() is not { } config) { _output.WriteLine("Skipped: Graph settings not found."); return; }
        var newest = await RawClient(config).Users[config.UserId].MailFolders["inbox"].Messages.GetAsync(c =>
        {
            c.QueryParameters.Select = ["id", "from"];
            c.QueryParameters.Orderby = ["receivedDateTime desc"];
            c.QueryParameters.Top = 60;
        });
        if (newest?.Value is not { Count: 60 } page || page[59].From?.EmailAddress?.Address is not { } sender)
        {
            _output.WriteLine("Skipped: the inbox has fewer than 60 emails.");
            return;
        }
        var target = page[59].Id;
        config.ExportForGraphClient();
        var graph = new GraphClient();

        var listed = await graph.GetUserEmailsAsync(MailFolder.Inbox);
        listed.Should().NotContain(e => e.Id == target, because: "a plain listing returns only the 50 newest");

        var found = await graph.GetUserEmailsAsync(MailFolder.Inbox, search: $"from:{sender}");
        found.Should().Contain(e => e.Id == target, because: "a search reaches the whole folder");
        found.Select(e => e.ReceivedDateTime).Should().BeInDescendingOrder(because: "the newest match comes first");

        var lastDay = await graph.GetUserEmailsAsync(MailFolder.Inbox, daysBack: 1, search: $"from:{sender}");
        lastDay.Should().OnlyContain(e => e.ReceivedDateTime >= DateTimeOffset.UtcNow.AddDays(-1).AddMinutes(-5),
            because: "a window given with a search still applies");
        lastDay.Select(e => e.Id).Should().BeSubsetOf(found.Select(e => e.Id));

        _output.WriteLine($"60th newest email: absent from the plain listing of {listed.Length}, "
            + $"found among {found.Length} sender matches ({lastDay.Length} in the last day)");
    }

    [Fact]
    public async Task Search_WithQuotesAndBackslashes_IsAcceptedByGraph()
    {
        // GraphClient.SearchClause's escaping against the real parser: a 400 here means it is wrong.
        if (Load() is not { } config) { _output.WriteLine("Skipped: Graph settings not found."); return; }
        config.ExportForGraphClient();

        var none = await new GraphClient().GetUserEmailsAsync(MailFolder.Inbox,
            search: @"subject:""zq ancela no such subject"" a\b zq");

        none.Should().BeEmpty();
    }

    private sealed record Config(string TenantId, string ClientId, string ClientSecret, string UserId)
    {
        /// <summary>GraphClient reads its settings from the environment, as it does in the function app.</summary>
        public void ExportForGraphClient()
        {
            Environment.SetEnvironmentVariable("GRAPH_TENANT_ID", TenantId);
            Environment.SetEnvironmentVariable("GRAPH_CLIENT_ID", ClientId);
            Environment.SetEnvironmentVariable("GRAPH_CLIENT_SECRET", ClientSecret);
            Environment.SetEnvironmentVariable("GRAPH_USER_ID", UserId);
        }
    }

    private static Config? Load()
    {
        // Same user-secrets store as the AppHost; an environment variable wins over it.
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<MailSearchLiveTests>()
            .AddEnvironmentVariables()
            .Build();
        string? Get(string name) =>
            configuration[name.ToUpperInvariant().Replace('-', '_')] ?? configuration[$"Parameters:{name}"];

        string?[] values = [Get("graph-tenant-id"), Get("graph-client-id"), Get("graph-client-secret"), Get("graph-user-id")];
        return values.Any(string.IsNullOrWhiteSpace)
            ? null
            : new Config(values[0]!, values[1]!, values[2]!, values[3]!);
    }

    private static GraphServiceClient RawClient(Config config) => new(
        new ClientSecretCredential(config.TenantId, config.ClientId, config.ClientSecret),
        ["https://graph.microsoft.com/.default"]);
}
