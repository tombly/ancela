using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using FluentAssertions;
using MailKit;
using MailKit.Search;
using MimeKit;
using MailFolder = Ancela.Agent.SemanticKernel.Plugins.GraphPlugin.MailFolder;

namespace Ancela.Agent.Tests;

/// <summary>
/// The parts of iCloud mail (ImapMailClient) that need no server: email ids, get_recent_emails'
/// search as IMAP SEARCH, an HTML body as text, which parts of an email are its attachments, and the
/// address it sends from.
/// ICloudMailLiveTests covers the calls against the real account.
/// </summary>
public class ImapMailTests
{
    [Theory]
    [InlineData(MailFolder.Inbox, 4821u, "inbox-4821")]
    [InlineData(MailFolder.Sent, 7u, "sent-7")]
    [InlineData(MailFolder.Deleted, 123456u, "deleted-123456")]
    public void AnEmailId_IsItsFolderAndUid_AndReadsBack(MailFolder folder, uint uid, string id)
    {
        ImapSearch.EmailId(folder, new UniqueId(uid)).Should().Be(id);
        ImapSearch.ParseEmailId(id).Should().Be((folder, new UniqueId(uid)));
    }

    [Theory]
    [InlineData("AAMkAGI2NGVhZTVlLTI1OGMtNDI1My1iYWIwLTdmMWVhYWQxMTU0NgBGAAAAAACdSk3UEDsLR6sNDbp2QFHjBwBvGLO2hHOVRqtVXTtlrDGuAAA=")]  // a Graph id
    [InlineData("junk-12")]     // a folder the tools don't read
    [InlineData("inbox-0")]
    [InlineData("inbox-abc")]
    [InlineData("4821")]
    public void AnIdFromAnywhereElse_IsRefused_WithWhatToDo(string id)
    {
        var act = () => ImapSearch.ParseEmailId(id);

        act.Should().Throw<InvalidOperationException>().WithMessage("*List the emails again*");
    }

    [Fact]
    public void Words_MustEachAppearSomewhere()
    {
        var query = ImapSearch.Query("invoice march");

        query.Should().BeOfType<BinarySearchQuery>().Which.Term.Should().Be(SearchTerm.And);
        Texts(query).Should().Equal((SearchTerm.MessageContains, "invoice"), (SearchTerm.MessageContains, "march"));
    }

    [Fact]
    public void From_NarrowsAWordToTheSender()
    {
        Texts(ImapSearch.Query("from:someone@example.com")).Should().Equal((SearchTerm.FromContains, "someone@example.com"));
    }

    [Fact]
    public void AQuotedPhrase_IsOneTerm_AndOrTakesEither()
    {
        var query = ImapSearch.Query("from:\"Bob Smith\" OR subject:\"weekly report\"");

        query.Should().BeOfType<BinarySearchQuery>().Which.Term.Should().Be(SearchTerm.Or);
        Texts(query).Should().Equal((SearchTerm.FromContains, "Bob Smith"), (SearchTerm.SubjectContains, "weekly report"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("AND")]
    public void NoTerms_MeansEveryEmail(string search) => ImapSearch.Query(search).Should().Be(SearchQuery.All);

    [Fact]
    public void AnHtmlBody_BecomesText_WithoutScriptsOrStyles()
    {
        var text = ImapMailClient.HtmlText(
            "<html><head><title>T</title><style>p{color:red}</style></head><body><p>Hello&nbsp;there,</p>"
            + "<script>steal()</script><div>Line   two<br>Line three</div><table><tr><td>a</td><td>b</td></tr></table></body></html>");

        text.Should().NotContain("steal").And.NotContain("color").And.NotContain("T\n");
        text.Should().Contain("Hello there,").And.Contain("Line two\nLine three").And.Contain("a b");
    }

    [Fact]
    public void Attachments_AreTheFiles_NotThePicturesTheBodyShows()
    {
        var builder = new BodyBuilder { HtmlBody = "<p>See <img src=\"cid:logo\"></p>" };
        var logo = builder.LinkedResources.Add("logo.png", new byte[] { 1, 2, 3 }, new ContentType("image", "png"));
        logo.ContentId = "logo";
        builder.Attachments.Add("report.xml.gz", new byte[] { 4, 5 }, new ContentType("application", "gzip"));
        var message = new MimeMessage { Body = builder.ToMessageBody() };

        ImapMailClient.Attachments(message).Select(a => a.Name).Should().Equal("report.xml.gz");
    }

    [Fact]
    public void AFileNotMarkedAsAnAttachment_StillCounts()
    {
        // Some report senders attach the file without a Content-Disposition, only a name.
        var zip = new MimePart("application", "zip") { Content = new MimeContent(new MemoryStream([1, 2])) };
        zip.ContentType.Name = "google.com!example.com!1!2.zip";
        var message = new MimeMessage { Body = new Multipart("mixed") { new TextPart("plain") { Text = "" }, zip } };

        ImapMailClient.Attachments(message).Select(a => a.Name).Should().Equal("google.com!example.com!1!2.zip");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("owner")]
    public void Sending_WithNoAddressToSendFrom_IsOff(string? from) =>
        FluentActions.Invoking(() => ImapMailClient.SendingAddress(from))
            .Should().Throw<InvalidOperationException>().WithMessage("*no address to send from*sending is off*");

    [Theory]
    [InlineData("owner@icloud.com")]
    [InlineData("owner@me.com")]
    [InlineData("Owner <owner@MAC.com>")]
    public void Sending_FromAnICloudAddress_IsOff(string from) =>
        FluentActions.Invoking(() => ImapMailClient.SendingAddress(from))
            .Should().Throw<InvalidOperationException>().WithMessage("*never sends from*");

    [Fact]
    public void Sending_IsFromTheOwnersDomain() =>
        ImapMailClient.SendingAddress("owner@example.com").Address.Should().Be("owner@example.com");

    /// <summary>The (term, text) of each text query, left to right.</summary>
    private static List<(SearchTerm, string)> Texts(SearchQuery query) => query switch
    {
        BinarySearchQuery binary => [.. Texts(binary.Left), .. Texts(binary.Right)],
        TextSearchQuery text => [(text.Term, text.Text)],
        _ => [],
    };
}
