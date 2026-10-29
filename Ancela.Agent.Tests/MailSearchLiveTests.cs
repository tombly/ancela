using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using Azure.Identity;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item;
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

        _output.WriteLine($"60th newest email: absent from the plain listing of {listed.Length}, "
            + $"found among {found.Length} sender matches");
    }

    [Fact]
    public async Task PlainListing_IsTheNewest50_WithNoDateCutoff()
    {
        // Runs on any inbox, unlike the test above. There is no date window, so the listing must be
        // exactly the folder's newest 50, however old the oldest of them is.
        if (Load() is not { } config) { _output.WriteLine("Skipped: Graph settings not found."); return; }
        var newest = await RawClient(config).Users[config.UserId].MailFolders["inbox"].Messages.GetAsync(c =>
        {
            c.QueryParameters.Select = ["id"];
            c.QueryParameters.Orderby = ["receivedDateTime desc"];
            c.QueryParameters.Top = 50;
        });
        config.ExportForGraphClient();

        var listed = await new GraphClient().GetUserEmailsAsync(MailFolder.Inbox);

        listed.Select(e => e.Id).Should().Equal(newest!.Value!.Select(m => m.Id));
        var oldestDays = listed.Length == 0 ? 0 : (int)(DateTimeOffset.UtcNow - listed[^1].ReceivedDateTime).TotalDays;
        _output.WriteLine($"{listed.Length} listed; the oldest is {oldestDays} days old");
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

    [Fact]
    public async Task ReadEmail_ReturnsTheWholeBody_FromTheFoldersAncelaReads()
    {
        // read_email exists because get_recent_emails has only the first 255 characters. Also checks
        // that the folder ids it compares parentFolderId with match Graph's, for Inbox and Sent.
        if (Load() is not { } config) { _output.WriteLine("Skipped: Graph settings not found."); return; }
        config.ExportForGraphClient();
        var graph = new GraphClient();

        var inbox = await graph.GetUserEmailsAsync(MailFolder.Inbox);
        if (inbox.FirstOrDefault(e => e.BodyPreview.Length >= 255) is not { } longEmail)
        {
            _output.WriteLine("Skipped: none of the 50 newest emails is longer than its preview.");
            return;
        }

        var email = await graph.GetEmailAsync(longEmail.Id);

        email.Id.Should().Be(longEmail.Id);
        email.Body.Length.Should().BeGreaterThan(longEmail.BodyPreview.Length, because: "the preview is cut at 255 characters");
        static string Words(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var opening = Words(longEmail.BodyPreview)[..30];
        Words(email.Body).Should().Contain(opening, because: "the preview is the start of the same body");

        var sent = await graph.GetUserEmailsAsync(MailFolder.Sent);
        if (sent.Length > 0)
            (await graph.GetEmailAsync(sent[0].Id)).Id.Should().Be(sent[0].Id);

        _output.WriteLine($"Inbox email: preview {longEmail.BodyPreview.Length} characters, body {email.Body.Length} "
            + $"(truncated: {email.Truncated}); Sent email read: {sent.Length > 0}");
    }

    [Fact]
    public async Task ReadAttachment_ReadsATextAttachment_ByTheNameReadEmailGives()
    {
        // read_email lists the files attached; read_attachment fetches one by that name and unpacks
        // it. Looks for a .zip, .gz or text file in the inbox, such as a DMARC report.
        if (Load() is not { } config) { _output.WriteLine("Skipped: Graph settings not found."); return; }
        var withAttachments = await RawClient(config).Users[config.UserId].MailFolders["inbox"].Messages.GetAsync(c =>
        {
            c.QueryParameters.Select = ["id"];
            c.QueryParameters.Filter = "hasAttachments eq true";
            c.QueryParameters.Top = 25;
        });
        config.ExportForGraphClient();
        var graph = new GraphClient();

        string[] readable = [".zip", ".gz", ".xml", ".csv", ".txt", ".ics", ".json"];
        foreach (var message in withAttachments?.Value ?? [])
        {
            var email = await graph.GetEmailAsync(message.Id!);
            if (email.Attachments.FirstOrDefault(n => readable.Any(e => n.EndsWith(e, StringComparison.OrdinalIgnoreCase))) is not { } name)
                continue;

            var attachment = await graph.GetAttachmentAsync(email.Id, name);

            attachment.Name.Should().Be(name);
            attachment.Files.Should().NotBeEmpty();
            attachment.Files[0].Text.Should().NotBeNullOrWhiteSpace();
            _output.WriteLine($"Read a {Path.GetExtension(name)} attachment: {attachment.Files.Length} file(s), "
                + $"{attachment.Files.Sum(f => f.Text.Length)} characters, truncated: {attachment.Files.Any(f => f.Truncated)}");
            return;
        }
        _output.WriteLine($"Skipped: none of the {withAttachments?.Value?.Count ?? 0} inbox emails with attachments has a text, .zip or .gz one.");
    }

    [Fact]
    public async Task TrashEmail_MovesAnEmailToDeletedItems()
    {
        // Needs Mail.ReadWrite (README, step 1): with Mail.Read alone, making the email is a 403.
        // Works on an email of its own, made in the Inbox rather than sent, so nothing leaves the
        // mailbox, nothing else in it is touched, and the test needs no permission but the one it
        // tests. The email is deleted at the end.
        if (Load() is not { } config) { _output.WriteLine("Skipped: Graph settings not found."); return; }
        var mailbox = RawClient(config).Users[config.UserId];
        var (made, subject) = await MakeTestEmailAsync(mailbox, "trash_email");
        config.ExportForGraphClient();
        var graph = new GraphClient();
        try
        {
            // A message made rather than received has no sender, so only the subject is checked.
            (await graph.MoveToDeletedItemsAsync(made)).Should().StartWith($"Moved to Deleted Items: \"{subject}\" from ",
                because: "the audit log records the result, and the id alone can't say which email it was");

            (await WithSubject(mailbox, "inbox", subject)).Should().BeEmpty();
            var trashed = await WithSubject(mailbox, "deleteditems", subject);
            trashed.Should().ContainSingle(because: "a trashed email can still be read and restored");
            (await graph.MoveToDeletedItemsAsync(trashed[0])).Should().StartWith($"That email is already in Deleted Items: \"{subject}\"");
            _output.WriteLine("Moved the test email from the Inbox to Deleted Items.");
        }
        finally
        {
            await DeleteTestEmailAsync(mailbox, subject);
        }
    }

    [Fact]
    public async Task MarkEmailRead_SetsTheFlag_BothWays_AndKeepsTheId()
    {
        // Needs Mail.ReadWrite, as trash_email does. Works on an email of its own, like the test above.
        if (Load() is not { } config) { _output.WriteLine("Skipped: Graph settings not found."); return; }
        var mailbox = RawClient(config).Users[config.UserId];
        var (made, subject) = await MakeTestEmailAsync(mailbox, "mark_email_read");
        config.ExportForGraphClient();
        var graph = new GraphClient();
        async Task<bool?> IsRead() => (await mailbox.Messages[made].GetAsync(c => c.QueryParameters.Select = ["isRead"]))?.IsRead;
        try
        {
            (await graph.MarkReadAsync(made, read: false)).Should().StartWith($"Marked as unread: \"{subject}\"");
            (await IsRead()).Should().BeFalse();

            // The same id still works: unlike a move, marking an email doesn't change its id, so a
            // task can mark a report read and then trash it.
            (await graph.MarkReadAsync(made, read: true)).Should().StartWith($"Marked as read: \"{subject}\"");
            (await IsRead()).Should().BeTrue();
            (await graph.MarkReadAsync(made, read: true)).Should().StartWith($"That email is already marked as read: \"{subject}\"");

            await graph.MoveToDeletedItemsAsync(made);
            var trashed = await WithSubject(mailbox, "deleteditems", subject);
            (await mailbox.Messages[trashed.Single()].GetAsync(c => c.QueryParameters.Select = ["isRead"]))!.IsRead
                .Should().BeTrue(because: "the move keeps the flag, so a report marked read isn't unread in Deleted Items");
            _output.WriteLine("Marked the test email unread, then read, then moved it to Deleted Items still read.");
        }
        finally
        {
            await DeleteTestEmailAsync(mailbox, subject);
        }
    }

    /// <summary>
    /// An email made in the Inbox rather than sent, so nothing leaves the mailbox and nothing else in
    /// it is touched. Made rather than received, it has no sender.
    /// </summary>
    private static async Task<(string Id, string Subject)> MakeTestEmailAsync(UserItemRequestBuilder mailbox, string tool)
    {
        var subject = $"Ancela {tool} test {Guid.NewGuid():N}";
        var made = await mailbox.MailFolders["inbox"].Messages.PostAsync(new Message
        {
            Subject = subject,
            Body = new ItemBody { ContentType = BodyType.Text, Content = $"Made by MailSearchLiveTests to test {tool}." },
            IsRead = true,
        });
        return (made!.Id!, subject);
    }

    private static async Task DeleteTestEmailAsync(UserItemRequestBuilder mailbox, string subject)
    {
        foreach (var folder in new[] { "inbox", "deleteditems" })
            foreach (var id in await WithSubject(mailbox, folder, subject))
                await mailbox.Messages[id].DeleteAsync();
    }

    private static async Task<string[]> WithSubject(UserItemRequestBuilder mailbox, string folder, string subject) =>
        ((await mailbox.MailFolders[folder].Messages.GetAsync(c =>
        {
            c.QueryParameters.Select = ["id"];
            c.QueryParameters.Filter = $"subject eq '{subject}'";
        }))?.Value ?? []).Select(m => m.Id!).ToArray();

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
