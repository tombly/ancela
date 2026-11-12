using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace Ancela.Agent.Tests;

/// <summary>
/// Live diagnostic for iCloud's calendar (CalDavCalendarClient) and contacts (CardDavContactsClient)
/// against the owner's account. Excluded from normal runs (Integration trait) and skips unless
/// Parameters:icloud-username and icloud-app-password are in user secrets:
///   dotnet test --filter "FullyQualifiedName~ICloudCalendarContactsLiveTests"
/// Prints counts only, no event, name or number. The one write, only with ANCELA_LIVE_SEND=1 (the
/// owner's OK each time), adds an event without an alert for tomorrow and afterwards deletes it, and
/// only it: it goes on the calendar shared with the owner, which the people it is shared with see.
/// </summary>
[Trait("Category", "Integration")]
public class ICloudCalendarContactsLiveTests(ITestOutputHelper _output) : IAsyncLifetime
{
    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

    private readonly string _token = $"z{Guid.NewGuid():N}"[..13];
    private (string User, string Password)? _account;

    public Task InitializeAsync()
    {
        var config = new ConfigurationBuilder().AddUserSecrets<ICloudCalendarContactsLiveTests>().AddEnvironmentVariables().Build();
        if (config["Parameters:icloud-username"] is { Length: > 0 } user && config["Parameters:icloud-app-password"] is { Length: > 0 } password)
        {
            _account = (user, password);
            Environment.SetEnvironmentVariable("ICLOUD_USERNAME", user);
            Environment.SetEnvironmentVariable("ICLOUD_APP_PASSWORD", password);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TheNextTwoWeeks_ReadFromEveryCalendar()
    {
        if (Skipped()) return;
        using var calendar = new CalDavCalendarClient();
        var now = DateTimeOffset.UtcNow;

        var events = await calendar.GetUserEventsAsync(now, now.AddDays(14), Pacific);

        _output.WriteLine($"calendars of events: {await calendar.CheckAsync()}; occurrences in the next 14 days: {events.Length}, all-day {events.Count(e => e.IsAllDay)}");
        events.Should().BeInAscendingOrder(e => e.Start);
        events.Should().OnlyContain(e => e.Start.Offset == Pacific.GetUtcOffset(e.Start) && e.End >= e.Start);
    }

    [Fact]
    public async Task ANewEvent_ReadsBackAtItsTime()
    {
        if (Skipped()) return;
        if (Environment.GetEnvironmentVariable("ANCELA_LIVE_SEND") != "1")
        {
            _output.WriteLine("Skipped: set ANCELA_LIVE_SEND=1 to add a test event to the shared calendar, which others see.");
            return;
        }
        using var calendar = new CalDavCalendarClient();
        var tomorrow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Pacific).Date.AddDays(1);
        var start = tomorrow.AddHours(10).ToString("yyyy-MM-ddTHH:mm:ss");

        var created = await calendar.CreateEventAsync($"Ancela live test {_token}", start, tomorrow.AddHours(10.5).ToString("yyyy-MM-ddTHH:mm:ss"), Pacific);

        var dayStart = new DateTimeOffset(tomorrow, Pacific.GetUtcOffset(tomorrow));
        var found = (await calendar.GetUserEventsAsync(dayStart, dayStart.AddDays(1), Pacific)).Where(e => e.Description.Contains(_token)).ToList();
        found.Should().ContainSingle().Which.Start.Should().Be(created.Start);
        found[0].Start.Hour.Should().Be(10, because: "the time is read on the user's clock");
        found[0].End.Should().Be(created.Start.AddMinutes(30));
    }

    [Fact]
    public async Task TheContacts_ReadWhole_AndANameFindsOne()
    {
        if (Skipped()) return;
        using var contacts = new CardDavContactsClient();

        var all = await contacts.GetUserContactsAsync(500);

        _output.WriteLine($"contacts: {all.Length}; with an email {all.Count(c => c.EmailAddresses.Length > 0)}, with a number {all.Count(c => c.Phones.Length > 0)}, " +
            $"number labels: {string.Join(", ", all.SelectMany(c => c.Phones).GroupBy(p => p.Label is "mobile" or "home" or "work" or "iPhone" or "main" or "other" ? p.Label : "own label").Select(g => $"{g.Key} {g.Count()}"))}");
        all.Should().NotBeEmpty().And.BeInAscendingOrder(c => c.DisplayName, StringComparer.OrdinalIgnoreCase);
        var someone = all.First(c => c.DisplayName.Length > 2);
        (await contacts.GetUserContactByNameAsync(someone.DisplayName[..3])).Should().NotBeNull();
    }

    private bool Skipped()
    {
        if (_account is not null)
            return false;
        _output.WriteLine("Skipped: iCloud settings not found in user secrets.");
        return true;
    }

    /// <summary>Deletes this test's events, and nothing else, from every calendar.</summary>
    public async Task DisposeAsync()
    {
        if (_account is null)
            return;

        using var dav = new ICloudDav(new Uri("https://caldav.icloud.com/"), ICloudDav.CalDav + "calendar-home-set", "calendar");
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_account.Value.User}:{_account.Value.Password}")));
        XNamespace d = ICloudDav.Dav, c = ICloudDav.CalDav;
        var home = await dav.HomeAsync();
        var calendars = ICloudDav.Responses(await dav.SendAsync("PROPFIND", home, 1, ICloudDav.Propfind(d + "resourcetype")), home)
            .Where(r => r.Response.Descendants(d + "resourcetype").Elements(c + "calendar").Any());
        // The next three days, matched by the token in the event's text: iCloud answered a SUMMARY
        // text-match with nothing, and the first run left its event behind (2026-11-11).
        var from = DateTime.UtcNow.Date;
        foreach (var (calendar, _) in calendars)
        {
            var query = new XElement(c + "calendar-query", new XElement(d + "prop", new XElement(c + "calendar-data")),
                new XElement(c + "filter", new XElement(c + "comp-filter", new XAttribute("name", "VCALENDAR"),
                    new XElement(c + "comp-filter", new XAttribute("name", "VEVENT"),
                        new XElement(c + "time-range", new XAttribute("start", $"{from:yyyyMMdd}T000000Z"), new XAttribute("end", $"{from.AddDays(3):yyyyMMdd}T000000Z"))))));
            foreach (var (href, _) in ICloudDav.Responses(await dav.SendAsync("REPORT", calendar, 1, query), calendar)
                .Where(r => r.Response.Descendants(c + "calendar-data").Any(data => data.Value.Contains(_token))))
            {
                (await http.DeleteAsync(href)).EnsureSuccessStatusCode();
                _output.WriteLine("removed 1 test event");
            }
        }
    }
}
