using System.Text;
using System.Xml.Linq;

namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

/// <summary>
/// The owner's iCloud contacts over CardDAV (<see cref="ICloudDav"/>): the same tools and results as
/// Graph's (<see cref="GraphClient"/>). iCloud keeps each contact as a vCard 3.0 (probed 2026-11-11:
/// 53 cards, one address book, under 1 KB each), all of them read on each call, about two seconds.
/// Every phone number is given with its label: 13 of those contacts had only a home or other number,
/// which a result of mobile and work numbers alone would have hidden.
/// </summary>
public sealed class CardDavContactsClient : IContactsClient, IDisposable
{
    private readonly ICloudDav _dav = new(new Uri("https://contacts.icloud.com/"), ICloudDav.CardDav + "addressbook-home-set", "contacts");

    /// <summary>A contact and the parts of its name a search matches, which the result doesn't show.</summary>
    public record Card(ContactModel Contact, string GivenName, string FamilyName);

    public async Task<ContactModel[]> GetUserContactsAsync(int maxResults = 100) =>
        [.. (await CardsAsync()).Select(c => c.Contact).OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase).Take(maxResults)];

    public async Task<ContactModel?> GetUserContactByNameAsync(string name) => Find(await CardsAsync(), name);

    /// <summary>For the health probe: how many contacts the account has.</summary>
    public async Task<int> CheckAsync() => (await CardsAsync()).Count;

    /// <summary>
    /// As Graph's search: a contact whose name, first name or last name starts with the text, the one
    /// whose name contains it whole first, else the first by name.
    /// </summary>
    public static ContactModel? Find(IEnumerable<Card> cards, string name)
    {
        var term = name.Trim();
        var matches = cards
            .Where(c => new[] { c.Contact.DisplayName, c.GivenName, c.FamilyName }.Any(n => n.StartsWith(term, StringComparison.OrdinalIgnoreCase)))
            .Select(c => c.Contact)
            .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return matches.FirstOrDefault(c => c.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)) ?? matches.FirstOrDefault();
    }

    /// <summary>One vCard as a contact, or null for a group (iCloud keeps groups as cards too).</summary>
    public static Card? Parse(string vcard)
    {
        var properties = Unfold(vcard).Select(Property).OfType<VCardProperty>().ToList();
        if (properties.Any(p => p.Name == "X-ADDRESSBOOKSERVER-KIND" && p.Value.Equals("group", StringComparison.OrdinalIgnoreCase)))
            return null;

        string First(string name) => properties.FirstOrDefault(p => p.Name == name)?.Value ?? string.Empty;
        var n = Components(First("N"));
        string Part(int i) => i < n.Count ? n[i] : string.Empty;
        var company = Components(First("ORG")).FirstOrDefault() ?? string.Empty;
        var displayName = Unescape(First("FN"));
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = $"{Part(1)} {Part(0)}".Trim() is { Length: > 0 } fromParts ? fromParts : company;

        // Apple labels a number by a property in its group ("item1.X-ABLabel"), when it has one.
        var labels = properties.Where(p => p.Name == "X-ABLABEL" && p.Group is not null)
            .GroupBy(p => p.Group!).ToDictionary(g => g.Key, g => Unescape(g.First().Value));
        var phones = properties.Where(p => p.Name == "TEL" && !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => new ContactPhone(
                p.Group is not null && labels.TryGetValue(p.Group, out var label) ? AppleLabel(label) : PhoneLabel(p.Types),
                Unescape(p.Value.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ? p.Value[4..] : p.Value)))
            .ToArray();

        return new Card(new ContactModel
        {
            DisplayName = displayName,
            EmailAddresses = [.. properties.Where(p => p.Name == "EMAIL" && !string.IsNullOrWhiteSpace(p.Value)).Select(p => Unescape(p.Value))],
            Phones = phones,
            CompanyName = company,
            JobTitle = Unescape(First("TITLE")),
        }, Part(1), Part(0));
    }

    private async Task<List<Card>> CardsAsync()
    {
        var home = await _dav.HomeAsync();
        var listing = await _dav.SendAsync("PROPFIND", home, 1, ICloudDav.Propfind(ICloudDav.Dav + "resourcetype"));
        var cards = new List<Card>();
        foreach (var (book, _) in ICloudDav.Responses(listing, home)
            .Where(r => r.Response.Descendants(ICloudDav.Dav + "resourcetype").Elements(ICloudDav.CardDav + "addressbook").Any()))
        {
            var report = await _dav.SendAsync("REPORT", book, 1, Query());
            foreach (var (_, response) in ICloudDav.Responses(report, book))
            {
                var vcard = response.Descendants(ICloudDav.CardDav + "address-data").FirstOrDefault()?.Value
                    ?? throw new InvalidOperationException("iCloud listed a contact without its details.");
                if (Parse(vcard) is { } card)
                    cards.Add(card);
            }
        }
        return cards;
    }

    /// <summary>Every card in the address book: a query with no filter matches all of them.</summary>
    private static XElement Query()
    {
        XNamespace d = ICloudDav.Dav, card = ICloudDav.CardDav;
        return new XElement(card + "addressbook-query", new XAttribute(XNamespace.Xmlns + "d", d.NamespaceName), new XAttribute(XNamespace.Xmlns + "card", card.NamespaceName),
            new XElement(d + "prop", new XElement(card + "address-data")));
    }

    private record VCardProperty(string? Group, string Name, IReadOnlyCollection<string> Types, string Value);

    /// <summary>A vCard's lines, each folded one joined back up (a line break and then a space or tab continues it).</summary>
    private static IEnumerable<string> Unfold(string vcard) =>
        vcard.Replace("\r\n", "\n").Replace("\n ", string.Empty).Replace("\n\t", string.Empty).Split('\n').Where(l => l.Length > 0);

    /// <summary>"item1.TEL;type=CELL;type=pref:+1 555 0100" as its group, name, types and value.</summary>
    private static VCardProperty? Property(string line)
    {
        var colon = -1;
        var quoted = false;
        for (var i = 0; i < line.Length && colon < 0; i++)
        {
            if (line[i] == '"')
                quoted = !quoted;
            else if (line[i] == ':' && !quoted)
                colon = i;
        }
        if (colon <= 0)
            return null;

        var parts = line[..colon].Split(';');
        var dot = parts[0].IndexOf('.');
        var types = parts.Skip(1)
            .SelectMany(p => p.Contains('=')
                ? p.StartsWith("type=", StringComparison.OrdinalIgnoreCase) ? p[5..].Trim('"').Split(',') : []
                : [p])
            .Select(t => t.ToUpperInvariant())
            .ToArray();
        return new VCardProperty(dot > 0 ? parts[0][..dot] : null, (dot > 0 ? parts[0][(dot + 1)..] : parts[0]).ToUpperInvariant(), types, line[(colon + 1)..]);
    }

    private static string PhoneLabel(IReadOnlyCollection<string> types)
    {
        var place = types.Contains("HOME") ? "home" : types.Contains("WORK") ? "work" : null;
        if (types.Contains("FAX"))
            return place is null ? "fax" : $"{place} fax";
        if (types.Contains("IPHONE"))
            return "iPhone";
        if (types.Contains("CELL"))
            return "mobile";
        if (types.Contains("MAIN"))
            return "main";
        if (types.Contains("PAGER"))
            return "pager";
        return place ?? "other";
    }

    // Apple's own labels are written as _$!<Mobile>!$_; any other is one the owner typed.
    private static string AppleLabel(string label) =>
        label.StartsWith("_$!<") && label.EndsWith(">!$_") ? label[4..^4].ToLowerInvariant() : label;

    /// <summary>A structured value's parts (N is family;given;…), split at each unescaped ';'.</summary>
    private static List<string> Components(string value)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
                current.Append(value[i]).Append(value[++i]);
            else if (value[i] == ';')
            {
                parts.Add(Unescape(current.ToString()));
                current.Clear();
            }
            else
                current.Append(value[i]);
        }
        parts.Add(Unescape(current.ToString()));
        return parts;
    }

    private static string Unescape(string value)
    {
        var text = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
                text.Append(value[++i] is 'n' or 'N' ? '\n' : value[i]);
            else
                text.Append(value[i]);
        }
        return text.ToString().Trim();
    }

    public void Dispose() => _dav.Dispose();
}
