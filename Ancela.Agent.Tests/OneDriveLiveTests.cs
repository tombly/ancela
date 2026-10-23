using System.Collections.Concurrent;
using System.Text;
using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using Azure.Identity;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Xunit.Abstractions;
using MailFolder = Ancela.Agent.SemanticKernel.Plugins.GraphPlugin.MailFolder;

namespace Ancela.Agent.Tests;

/// <summary>
/// Live diagnostic for save_email_to_onedrive against the real tenant. Excluded from normal runs
/// (Integration trait) and skips unless the Graph app settings (Parameters:graph-* in user secrets,
/// or the GRAPH_* environment variables) and the granted folder's GRAPH_ONEDRIVE_DRIVE_ID /
/// GRAPH_ONEDRIVE_FOLDER_ID are set:
///   dotnet test --filter "FullyQualifiedName~OneDriveLiveTests"
/// Pins what the feature relies on: the app can write only inside the granted folder, Graph converts
/// both the .eml and the body-only HTML, and the SDK does not carry the bearer token across the
/// conversion's redirect to a download host. Cleans up everything it creates; prints no email content.
/// </summary>
[Trait("Category", "Integration")]
public class OneDriveLiveTests(ITestOutputHelper _output)
{
    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

    [Fact]
    public async Task ConversionRedirect_DoesNotCarryTheToken()
    {
        if (Load() is not { } config) { _output.WriteLine("Skipped: Graph/OneDrive settings not found."); return; }
        var (client, spy) = BuildSpiedClient(config);

        var sources = new (string Extension, byte[] Content)[]
        {
            (".eml", SyntheticEml()),
            (".html", EmailPdf.Utf8WithBom(EmailPdf.RenderHtml(SyntheticMessage(), ["big.zip"], Pacific))),
        };
        foreach (var (extension, content) in sources)
        {
            var item = await client.Drives[config.DriveId].Items[config.FolderId]
                .ItemWithPath($"ancela-live-{Guid.NewGuid():N}{extension}").Content.PutAsync(new MemoryStream(content));
            try
            {
                spy.Requests.Clear();
                await using var stream = await client.Drives[config.DriveId].Items[item!.Id].Content
                    .GetAsync(c => c.QueryParameters.Format = "pdf");
                using var pdf = new MemoryStream();
                await stream!.CopyToAsync(pdf);

                pdf.ToArray().AsSpan().StartsWith("%PDF-"u8).Should().BeTrue(because: $"Graph converts {extension} to PDF");
                var path = Path.Combine(Path.GetTempPath(), $"ancela-onedrive-live{extension}.pdf");
                await File.WriteAllBytesAsync(path, pdf.ToArray());
                _output.WriteLine($"{extension}: {pdf.Length:N0}-byte PDF written to {path}");

                var onGraph = spy.Requests.Where(r => r.Host == "graph.microsoft.com").ToList();
                var offGraph = spy.Requests.Where(r => r.Host != "graph.microsoft.com").ToList();
                onGraph.Should().NotBeEmpty().And.OnlyContain(r => r.HasAuthorization,
                    because: "the spy has to see the token on Graph calls for its absence below to mean anything");
                offGraph.Should().NotBeEmpty(because: "Graph answers ?format=pdf with a redirect to a download host");
                offGraph.Should().OnlyContain(r => !r.HasAuthorization,
                    because: "the pre-authenticated download URL must not receive the Graph token");
                _output.WriteLine($"  redirect host(s): {string.Join(", ", offGraph.Select(r => r.Host).Distinct())}, token not sent");
            }
            finally
            {
                await client.Drives[config.DriveId].Items[item!.Id].DeleteAsync();
            }
        }
    }

    [Fact]
    public async Task WritingOutsideTheGrantedFolder_IsDenied()
    {
        if (Load() is not { } config) { _output.WriteLine("Skipped: Graph/OneDrive settings not found."); return; }
        var (client, _) = BuildSpiedClient(config);

        DriveItem? leaked = null;
        try
        {
            leaked = await client.Drives[config.DriveId].Root.ItemWithPath($"ancela-live-outside-{Guid.NewGuid():N}.txt")
                .Content.PutAsync(new MemoryStream("If you can read this, the grant is not scoped."u8.ToArray()));
        }
        catch (ODataError e)
        {
            e.ResponseStatusCode.Should().Be(403);
            _output.WriteLine("write at the drive root: 403");
            return;
        }
        finally
        {
            if (leaked?.Id is { } id)
                await client.Drives[config.DriveId].Items[id].DeleteAsync();
        }
        Assert.Fail("The app wrote at the drive root: its OneDrive access is no longer limited to the granted folder.");
    }

    [Fact]
    public async Task AttachmentListing_WithSelect_ReturnsNamesWithoutContent()
    {
        // The body-only path lists attachment names this way; without $select, file attachments
        // come back with their bytes, which is what that path exists to avoid.
        if (Load() is not { } config) { _output.WriteLine("Skipped: Graph/OneDrive settings not found."); return; }
        var (client, _) = BuildSpiedClient(config);

        var withAttachments = await client.Users[config.UserId].MailFolders["inbox"].Messages.GetAsync(c =>
        {
            c.QueryParameters.Filter = "hasAttachments eq true";
            c.QueryParameters.Select = ["id"];
            c.QueryParameters.Top = 1;
        });
        if (withAttachments?.Value?.FirstOrDefault()?.Id is not { } messageId)
        {
            _output.WriteLine("Skipped: no inbox email with attachments.");
            return;
        }

        var attachments = await client.Users[config.UserId].Messages[messageId].Attachments
            .GetAsync(c => c.QueryParameters.Select = ["name", "isInline"]);

        attachments!.Value.Should().NotBeEmpty().And.OnlyContain(a => !string.IsNullOrEmpty(a.Name));
        attachments.Value!.OfType<FileAttachment>().Should().OnlyContain(a => a.ContentBytes == null);
        _output.WriteLine($"{attachments.Value!.Count} attachment(s) listed, names only");
    }

    [Fact]
    public async Task SavesTheNewestInboxEmail_AndRemovesItsTemporaryFile()
    {
        if (Load() is not { } config) { _output.WriteLine("Skipped: Graph/OneDrive settings not found."); return; }
        var (client, _) = BuildSpiedClient(config);
        config.ExportForGraphClient();
        var graph = new GraphClient();

        var email = (await graph.GetUserEmailsAsync(MailFolder.Inbox)).FirstOrDefault();
        if (email is null) { _output.WriteLine("Skipped: the inbox is empty."); return; }

        // Never replace, then delete, a PDF the owner saved for real.
        var expectedName = EmailPdf.FileName(email.Subject, email.ReceivedDateTime, Pacific);
        if ((await FolderNamesAsync(client, config)).Contains(expectedName))
        {
            _output.WriteLine("Skipped: the newest email has already been saved to the folder.");
            return;
        }

        SavedEmailModel? saved = null;
        try
        {
            // Never the linked images: downloading them would tell a real sender the email was opened.
            saved = await graph.SaveEmailAsPdfAsync(email.Id, Pacific, includeLinkedImages: false);

            saved.FileName.Should().Be(expectedName);
            saved.WebUrl.Should().StartWith("https://");
            var names = await FolderNamesAsync(client, config);
            names.Should().Contain(expectedName);
            names.Should().NotContain(n => n.StartsWith("ancela-") && n.EndsWith(".html"),
                because: "the temporary source file is deleted after conversion");
            _output.WriteLine($"saved ({saved.LinkedImages} linked images left out, {saved.ImagesMissing} missing); temporary file removed");
        }
        finally
        {
            var children = await client.Drives[config.DriveId].Items[config.FolderId].Children
                .GetAsync(c => c.QueryParameters.Select = ["id", "name"]);
            if (children?.Value?.FirstOrDefault(i => i.Name == expectedName)?.Id is { } id)
                await client.Drives[config.DriveId].Items[id].DeleteAsync();
        }
    }

    private static async Task<HashSet<string>> FolderNamesAsync(GraphServiceClient client, Config config)
    {
        var children = await client.Drives[config.DriveId].Items[config.FolderId].Children
            .GetAsync(c => c.QueryParameters.Select = ["name"]);
        return children?.Value?.Select(i => i.Name ?? string.Empty).ToHashSet() ?? [];
    }

    private sealed record Config(string TenantId, string ClientId, string ClientSecret, string UserId, string DriveId, string FolderId)
    {
        /// <summary>GraphClient reads its settings from the environment, as it does in the function app.</summary>
        public void ExportForGraphClient()
        {
            Environment.SetEnvironmentVariable("GRAPH_TENANT_ID", TenantId);
            Environment.SetEnvironmentVariable("GRAPH_CLIENT_ID", ClientId);
            Environment.SetEnvironmentVariable("GRAPH_CLIENT_SECRET", ClientSecret);
            Environment.SetEnvironmentVariable("GRAPH_USER_ID", UserId);
            Environment.SetEnvironmentVariable("GRAPH_ONEDRIVE_DRIVE_ID", DriveId);
            Environment.SetEnvironmentVariable("GRAPH_ONEDRIVE_FOLDER_ID", FolderId);
        }
    }

    private static Config? Load()
    {
        // Same user-secrets store as the AppHost; an environment variable wins over it.
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<OneDriveLiveTests>()
            .AddEnvironmentVariables()
            .Build();
        string? Get(string name) =>
            configuration[name.ToUpperInvariant().Replace('-', '_')] ?? configuration[$"Parameters:{name}"];

        string?[] values =
        [
            Get("graph-tenant-id"), Get("graph-client-id"), Get("graph-client-secret"), Get("graph-user-id"),
            Get("graph-onedrive-drive-id"), Get("graph-onedrive-folder-id"),
        ];
        return values.Any(string.IsNullOrWhiteSpace)
            ? null
            : new Config(values[0]!, values[1]!, values[2]!, values[3]!, values[4]!, values[5]!);
    }

    /// <summary>A Graph client whose innermost handler records each request as it goes out, redirects included.</summary>
    private static (GraphServiceClient Client, RequestSpy Spy) BuildSpiedClient(Config config)
    {
        var spy = new RequestSpy();
        var handlers = GraphClientFactory.CreateDefaultHandlers();
        handlers.Add(spy);
        var credential = new ClientSecretCredential(config.TenantId, config.ClientId, config.ClientSecret);
        return (new GraphServiceClient(GraphClientFactory.Create(handlers), credential, ["https://graph.microsoft.com/.default"]), spy);
    }

    private sealed class RequestSpy : DelegatingHandler
    {
        public ConcurrentQueue<(string Host, bool HasAuthorization)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue((request.RequestUri!.Host, request.Headers.Authorization is not null));
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static Message SyntheticMessage() => new()
    {
        Subject = "Ancela live test <script>x</script>",
        From = new Recipient { EmailAddress = new EmailAddress { Name = "Ancela Live Test", Address = "probe@example.com" } },
        ToRecipients = [new Recipient { EmailAddress = new EmailAddress { Name = "Owner", Address = "owner@example.com" } }],
        SentDateTime = DateTimeOffset.UtcNow,
        Body = new ItemBody
        {
            ContentType = BodyType.Html,
            Content = "<html><head><style>h2{color:#2b579a}</style></head><body><h2>Body-only rendering</h2>"
                + "<p>Non-ASCII survives the BOM: café — naïve — 東京 — 😀</p></body></html>",
        },
    };

    private static byte[] SyntheticEml() => Encoding.UTF8.GetBytes(
        "From: Ancela Live Test <probe@example.com>\r\n" +
        "To: Owner <owner@example.com>\r\n" +
        "Subject: Ancela live test\r\n" +
        $"Date: {DateTimeOffset.UtcNow:R}\r\n" +
        "MIME-Version: 1.0\r\n" +
        "Content-Type: text/html; charset=utf-8\r\n\r\n" +
        "<html><body><h2 style=\"color:#2b579a\">Ancela live test</h2><p>Synthetic message; example.com only.</p></body></html>\r\n");
}
