using System.Diagnostics;
using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using FluentAssertions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using MimeKit;
using Moq;
using Xunit.Abstractions;
using MailFolder = Ancela.Agent.SemanticKernel.Plugins.GraphPlugin.MailFolder;

namespace Ancela.Agent.Tests;

/// <summary>
/// Live diagnostic for iCloud mail (ImapMailClient) against the owner's account. Excluded from normal
/// runs (Integration trait) and skips unless Parameters:icloud-username and icloud-app-password are
/// in user secrets:
///   dotnet test --filter "FullyQualifiedName~ICloudMailLiveTests"
/// Each test puts its own email in the Inbox (IMAP APPEND: nothing is sent) and afterwards deletes it,
/// and only it, for good, from wherever it ended up. Prints no content of any other email.
/// Sending is tried only with ANCELA_LIVE_SEND=1 (and Parameters:icloud-from-address), and goes from
/// and to the owner's own domain address.
/// </summary>
[Trait("Category", "Integration")]
public class ICloudMailLiveTests(ITestOutputHelper _output) : IAsyncLifetime
{
    private static readonly TimeSpan SearchWait = TimeSpan.FromSeconds(90);

    private readonly string _token = $"z{Guid.NewGuid():N}"[..13];
    private string Subject => $"Ancela live test {_token}";
    private (string User, string Password)? _account;
    private string? _from;
    private ImapMailClient? _client;

    public Task InitializeAsync()
    {
        var config = new ConfigurationBuilder().AddUserSecrets<ICloudMailLiveTests>().AddEnvironmentVariables().Build();
        if (config["Parameters:icloud-username"] is { Length: > 0 } user && config["Parameters:icloud-app-password"] is { Length: > 0 } password)
        {
            _account = (user, password);
            Environment.SetEnvironmentVariable("ICLOUD_USERNAME", user);
            Environment.SetEnvironmentVariable("ICLOUD_APP_PASSWORD", password);
            _from = config["Parameters:icloud-from-address"];
            Environment.SetEnvironmentVariable("ICLOUD_FROM_ADDRESS", _from);
            _client = new ImapMailClient(new LinkedImageFetcher(), Pdf.Object);
        }
        return Task.CompletedTask;
    }

    private Mock<IPdfConverter> Pdf { get; } = new();

    [Fact]
    public async Task AWordInTheBody_FindsTheEmail_AndSoDoesItsSender()
    {
        if (await AppendAsync() is not { } client) return;

        var (byBody, waited) = await FindAsync(client, MailFolder.Inbox, $"{_token}body");
        _output.WriteLine($"found by a body word after {waited.TotalSeconds:0} s");
        byBody.Should().ContainSingle(e => e.Subject == Subject);
        byBody[0].BodyPreview.Should().Contain($"{_token}body", because: "the preview is the start of the body");
        byBody[0].IsRead.Should().BeFalse();

        (await client.GetUserEmailsAsync(MailFolder.Inbox, "from:ancela-test@example.com")).Should().Contain(e => e.Subject == Subject);
        (await client.GetUserEmailsAsync(MailFolder.Inbox, null)).Should().Contain(e => e.Subject == Subject, because: "a plain listing has the newest 50");
    }

    [Fact]
    public async Task ReadEmail_GivesTheWholeBody_AndReadAttachment_TheFile()
    {
        if (await AppendAsync() is not { } client) return;
        var id = (await FindAsync(client, MailFolder.Inbox, $"subject:\"{Subject}\"")).Emails.Single().Id;

        var email = await client.GetEmailAsync(id);
        email.Body.Should().Contain($"{_token}body").And.Contain("the last line");
        email.Attachments.Should().Equal($"notes-{_token}.csv");

        var attachment = await client.GetAttachmentAsync(id, $"notes-{_token}.csv");
        attachment.Files.Should().ContainSingle().Which.Text.Should().Contain("date,amount");

        (await client.GetUserEmailsAsync(MailFolder.Inbox, $"subject:\"{Subject}\"")).Single().IsRead
            .Should().BeFalse(because: "reading an email must not mark it read");
    }

    [Fact]
    public async Task MarkRead_AndTrash_KeepTheFlag_AndMoveIt()
    {
        if (await AppendAsync() is not { } client) return;
        var id = (await FindAsync(client, MailFolder.Inbox, $"subject:\"{Subject}\"")).Emails.Single().Id;

        (await client.MarkReadAsync(id, true)).Should().StartWith("Marked as read");
        (await client.MarkReadAsync(id, true)).Should().StartWith("That email is already marked as read");
        (await client.MarkReadAsync(id, false)).Should().StartWith("Marked as unread");
        (await client.MoveToDeletedItemsAsync(id)).Should().StartWith("Moved to Deleted Messages").And.Contain(Subject);

        (await client.GetUserEmailsAsync(MailFolder.Inbox, $"subject:\"{Subject}\"")).Should().BeEmpty();
        var (deleted, _) = await FindAsync(client, MailFolder.Deleted, $"subject:\"{Subject}\"");
        deleted.Should().ContainSingle().Which.IsRead.Should().BeFalse(because: "the move keeps the flag");
        deleted[0].Id.Should().StartWith("deleted-");
    }

    [Fact]
    public async Task RenderEmail_WritesItsEmbeddedPictureIntoThePage()
    {
        if (await AppendAsync() is not { } client) return;
        var id = (await FindAsync(client, MailFolder.Inbox, $"subject:\"{Subject}\"")).Emails.Single().Id;
        byte[]? page = null;
        Pdf.Setup(p => p.ConvertHtmlToPdfAsync(It.IsAny<byte[]>())).Callback<byte[]>(b => page = b).ReturnsAsync("%PDF-1.7"u8.ToArray());

        var pdf = await client.RenderEmailAsPdfAsync(id, TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"), includeLinkedImages: false);

        var html = System.Text.Encoding.UTF8.GetString(page!);
        html.Should().Contain(Subject).And.Contain("data:image/png;base64,", because: "the cid: picture is written into the page");
        pdf.ImagesMissing.Should().Be(0);
    }

    [Fact]
    public async Task Send_GoesOut_AndOneCopyLandsInSent()
    {
        if (_client is null || _from is null || Environment.GetEnvironmentVariable("ANCELA_LIVE_SEND") != "1")
        {
            _output.WriteLine("Skipped: set ANCELA_LIVE_SEND=1 and Parameters:icloud-from-address to send a test email to the owner's own address.");
            return;
        }

        var result = await _client.SendEmailAsync(_from, Subject, $"{_token}body sent by ICloudMailLiveTests.");

        result.Should().StartWith("Email sent successfully");
        var (sent, waited) = await FindAsync(_client, MailFolder.Sent, $"subject:\"{Subject}\"");
        _output.WriteLine($"Sent copies after {waited.TotalSeconds:0} s: {sent.Length}");
        // The copy delivered to the Inbox shows what iCloud sent, where Sent holds the one Ancela wrote.
        // Sent from and to the owner's domain address, it arrived within the run (2026-11-11); sent to
        // the old account's own iCloud address, it never had.
        var (delivered, arrived) = await FindAsync(_client, MailFolder.Inbox, $"subject:\"{Subject}\"");
        _output.WriteLine($"Delivered after {arrived.TotalSeconds:0} s: {delivered.Length}");
        delivered.Should().ContainSingle().Which.From.Should().Be(_from, because: "Ancela sends only from the owner's domain, never the iCloud address");
        // iCloud keeps no copy of its own (tried 2026-11-05), so Ancela's is the only one.
        (await _client.GetUserEmailsAsync(MailFolder.Sent, $"subject:\"{Subject}\"")).Should().ContainSingle(because: "one copy, not iCloud's and Ancela's")
            .Which.From.Should().Be(_from);
    }

    /// <summary>Puts this test's email in the Inbox, unread: text, a CSV and an embedded picture.</summary>
    private async Task<ImapMailClient?> AppendAsync()
    {
        if (_client is null)
        {
            _output.WriteLine("Skipped: iCloud settings not found in user secrets.");
            return null;
        }

        var builder = new BodyBuilder
        {
            TextBody = $"{_token}body is the first word.\nA second line.\nAnd the last line.",
            HtmlBody = $"<p>{_token}body is the first word.</p><p><img src=\"cid:pixel-{_token}\" width=\"40\" height=\"40\"></p><p>And the last line.</p>",
        };
        var picture = builder.LinkedResources.Add($"pixel-{_token}.png", Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGP8z8DAwMDAxMDAwMDAAAANHQEDasKb6QAAAABJRU5ErkJggg=="), new ContentType("image", "png"));
        picture.ContentId = $"pixel-{_token}";
        builder.Attachments.Add($"notes-{_token}.csv", "date,amount\n2026-11-05,12.50\n"u8.ToArray(), new ContentType("text", "csv"));
        var message = new MimeMessage { Subject = Subject, Body = builder.ToMessageBody(), Date = DateTimeOffset.UtcNow };
        message.From.Add(new MailboxAddress("Ancela live test", "ancela-test@example.com"));
        message.To.Add(MailboxAddress.Parse(_account!.Value.User));

        using var imap = await SignInAsync();
        await imap.Inbox.OpenAsync(FolderAccess.ReadWrite);
        await imap.Inbox.AppendAsync(new AppendRequest(message, MessageFlags.None));
        await imap.DisconnectAsync(true);
        return _client;
    }

    /// <summary>Searches until the test's email appears: iCloud may index a new email's body after a delay.</summary>
    private async Task<(EmailModel[] Emails, TimeSpan Waited)> FindAsync(ImapMailClient client, MailFolder folder, string search)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var found = (await client.GetUserEmailsAsync(folder, search)).Where(e => e.Subject == Subject).ToArray();
            if (found.Length > 0 || clock.Elapsed > SearchWait)
                return (found, clock.Elapsed);
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }

    private async Task<ImapClient> SignInAsync()
    {
        var imap = new ImapClient();
        await imap.ConnectAsync("imap.mail.me.com", 993, SecureSocketOptions.SslOnConnect);
        var user = _account!.Value.User;
        await imap.AuthenticateAsync(user.Contains('@') ? user[..user.IndexOf('@')] : user, _account.Value.Password);
        return imap;
    }

    /// <summary>Deletes this test's emails for good, and nothing else, from the Inbox, Junk, Sent and Deleted Messages.</summary>
    public async Task DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
        if (_account is null)
            return;

        using var imap = await SignInAsync();
        var folders = await imap.GetFoldersAsync(imap.PersonalNamespaces[0]);
        foreach (var folder in folders.Where(f => f.Attributes.HasFlag(FolderAttributes.Inbox) || f.FullName is "INBOX" or "Junk"
            || f.Attributes.HasFlag(FolderAttributes.Sent) || f.Attributes.HasFlag(FolderAttributes.Trash)))
        {
            await folder.OpenAsync(FolderAccess.ReadWrite);
            var mine = await folder.SearchAsync(SearchQuery.SubjectContains(_token));
            if (mine.Count == 0)
                continue;
            await folder.StoreAsync(mine, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true });
            await folder.ExpungeAsync(mine);  // UID EXPUNGE: only these
            _output.WriteLine($"removed {mine.Count} test email(s) from {folder.FullName}");
        }
        await imap.DisconnectAsync(true);
    }
}
