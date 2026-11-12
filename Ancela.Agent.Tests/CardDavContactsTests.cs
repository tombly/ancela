using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using FluentAssertions;

namespace Ancela.Agent.Tests;

/// <summary>
/// The parts of iCloud's contacts (CardDavContactsClient) that need no server: an iCloud vCard 3.0 as
/// a contact, every number with its label, and the name search Graph's matched. The cards are made up,
/// in the shape iCloud writes them (probed 2026-11-11).
/// </summary>
public class CardDavContactsTests
{
    private const string Sam = """
        BEGIN:VCARD
        VERSION:3.0
        PRODID:-//Apple Inc.//iPhone OS 18.0//EN
        N:Rivera;Sam;;;
        FN:Sam Rivera
        ORG:Example\, Inc.;Design
        TITLE:Engineer
        EMAIL;type=INTERNET;type=HOME;type=pref:sam@example.com
        EMAIL;type=INTERNET;type=WORK:sam.rivera@example.org
        TEL;type=IPHONE;type=CELL;type=VOICE;type=pref:+1 555 0100
        TEL;type=HOME;type=VOICE:+1 555 0101
        item1.TEL:+1 555 0102
        item1.X-ABLabel:Grandma's
        item2.TEL:+1 555 0103
        item2.X-ABLabel:_$!<Other>!$_
        TEL;type=WORK;type=FAX:+1 555 0104
        TEL;type=CELL:(555) 010-0105
        NOTE:Not part of the result
        UID:5C2F0000-0000-0000-0000-000000000001
        END:VCARD
        """;

    private static CardDavContactsClient.Card Parse(string vcard) => CardDavContactsClient.Parse(vcard.Replace("\n", "\r\n"))!;

    [Fact]
    public void AnICloudCard_IsAContact_WithEveryNumberLabelled()
    {
        var contact = Parse(Sam).Contact;

        contact.DisplayName.Should().Be("Sam Rivera");
        contact.CompanyName.Should().Be("Example, Inc.");
        contact.JobTitle.Should().Be("Engineer");
        contact.EmailAddresses.Should().Equal("sam@example.com", "sam.rivera@example.org");
        contact.Phones.Should().Equal(
            new ContactPhone("iPhone", "+1 555 0100"),
            new ContactPhone("home", "+1 555 0101"),
            new ContactPhone("Grandma's", "+1 555 0102"),
            new ContactPhone("other", "+1 555 0103"),
            new ContactPhone("work fax", "+1 555 0104"),
            new ContactPhone("mobile", "(555) 010-0105"));
    }

    [Fact]
    public void AFoldedLine_IsJoinedBackUp() =>
        Parse("BEGIN:VCARD\nVERSION:3.0\nFN:Alexandra Very\n  Long Name\nN:Name;Alexandra;;;\nEND:VCARD").Contact.DisplayName
            .Should().Be("Alexandra Very Long Name");

    [Fact]
    public void ACardWithoutAName_IsNamedByItsPartsOrItsCompany()
    {
        Parse("BEGIN:VCARD\nVERSION:3.0\nN:Rivera;Sam;;;\nEND:VCARD").Contact.DisplayName.Should().Be("Sam Rivera");
        Parse("BEGIN:VCARD\nVERSION:3.0\nN:;;;;\nORG:Example Clinic;\nTEL;type=MAIN:+1 555 0110\nEND:VCARD").Contact
            .Should().Match<ContactModel>(c => c.DisplayName == "Example Clinic" && c.Phones.Single().Label == "main");
    }

    [Fact]
    public void AGroup_IsNotAContact() =>
        CardDavContactsClient.Parse("BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Family\r\nX-ADDRESSBOOKSERVER-KIND:group\r\nEND:VCARD").Should().BeNull();

    [Fact]
    public void ATelUri_IsJustTheNumber() =>
        Parse("BEGIN:VCARD\nVERSION:4.0\nFN:Pat\nTEL;VALUE=uri;TYPE=cell:tel:+1-555-0120\nEND:VCARD").Contact.Phones
            .Should().Equal(new ContactPhone("mobile", "+1-555-0120"));

    [Theory]
    [InlineData("sam", "Sam Rivera")]
    [InlineData("Riv", "Sam Rivera")]
    [InlineData("  sam rivera ", "Sam Rivera")]
    [InlineData("Jo", "Jo Lee")]
    public void AName_FindsTheContactWhoseNameStartsWithIt(string search, string found) =>
        CardDavContactsClient.Find(Cards(), search)!.DisplayName.Should().Be(found);

    [Fact]
    public void ANameNoContactHas_FindsNone() => CardDavContactsClient.Find(Cards(), "Taylor").Should().BeNull();

    private static List<CardDavContactsClient.Card> Cards() =>
    [
        Parse(Sam),
        // "Jo" starts both names; the first of them by name is the one found, as Graph's search returned one.
        Parse("BEGIN:VCARD\nVERSION:3.0\nN:Adams;Jordan;;;\nFN:Jordan Adams\nEND:VCARD"),
        Parse("BEGIN:VCARD\nVERSION:3.0\nN:Lee;Jo;;;\nFN:Jo Lee\nEND:VCARD"),
    ];
}
