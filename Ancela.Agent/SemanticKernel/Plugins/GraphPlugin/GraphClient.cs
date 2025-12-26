using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

public interface IGraphClient
{
    Task<EventEntry[]> GetUserEventsAsync(DateTimeOffset start, DateTimeOffset end);
    Task<EmailEntry[]> GetUserEmailsAsync(int maxResults = 10);
    Task<ContactEntry[]> GetUserContactsAsync(int maxResults = 50);
    Task<ContactEntry?> GetUserContactByNameAsync(string name);
    Task<string> SendEmailAsync(string toAddress, string subject, string body);
}

/// <summary>
/// Service for interacting with Microsoft Graph API.
/// </summary>
public class GraphClient : IGraphClient
{
    /// <summary>
    /// The client secret credential for app-only authentication.
    /// </summary>
    private readonly ClientSecretCredential _clientSecretCredential;

    /// <summary>
    /// The Graph service client for app-only authentication.
    /// </summary>
    private readonly GraphServiceClient _appClient;

    /// <summary>
    /// The Entra ID of the user.
    /// </summary>
    private readonly string _entraUserId;

    public GraphClient()
    {
        _entraUserId = Environment.GetEnvironmentVariable("GRAPH_USER_ID") ?? throw new Exception("GRAPH_USER_ID not set");

        var tenantId = Environment.GetEnvironmentVariable("GRAPH_TENANT_ID") ?? throw new Exception("GRAPH_TENANT_ID not set");
        var clientId = Environment.GetEnvironmentVariable("GRAPH_CLIENT_ID") ?? throw new Exception("GRAPH_CLIENT_ID not set");
        var clientSecret = Environment.GetEnvironmentVariable("GRAPH_CLIENT_SECRET") ?? throw new Exception("GRAPH_CLIENT_SECRET not set");

        _clientSecretCredential = new ClientSecretCredential(tenantId, clientId, clientSecret);

        // Use the default scope, which will request the scopes configured on the
        // app registration
        _appClient = new GraphServiceClient(_clientSecretCredential, ["https://graph.microsoft.com/.default"]);
    }

    public async Task<EventEntry[]> GetUserEventsAsync(DateTimeOffset start, DateTimeOffset end)
    {
        var startDate = start.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
        var endDate = end.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

        // Get all calendars for the user.
        var calendars = await _appClient.Users[_entraUserId].Calendars.GetAsync();

        var allEvents = new List<Event>();

        if (calendars?.Value != null)
        {
            // Iterate through each calendar and get events.
            foreach (var calendar in calendars.Value)
            {
                var events = await _appClient.Users[_entraUserId].Calendars[calendar.Id].Events.GetAsync((config) =>
                {
                    // Request specific properties.
                    config.QueryParameters.Select = ["subject", "start", "end", "organizer"];
                    // Filter events that overlap with the date range (including multi-day events).
                    // An event overlaps if it starts before the range ends AND ends after the range starts.
                    config.QueryParameters.Filter = $"start/dateTime lt '{endDate}' and end/dateTime gt '{startDate}'";
                    // Get at most 50 results per calendar.
                    config.QueryParameters.Top = 50;
                    // Sort by start time.
                    config.QueryParameters.Orderby = ["start/dateTime"];
                });

                if (events?.Value != null)
                    allEvents.AddRange(events.Value);
            }
        }

        // Sort all events by start time and map to model.
        return allEvents
            .Where(e => e.Start?.DateTime != null && e.End?.DateTime != null)
            .OrderBy(e => e.Start!.DateTime)
            .Select(e => new EventEntry
            {
                Description = e.Subject ?? string.Empty,
                Start = DateTimeOffset.Parse(e.Start!.DateTime!),
                End = DateTimeOffset.Parse(e.End!.DateTime!)
            }).ToArray();
    }

    public async Task<EmailEntry[]> GetUserEmailsAsync(int maxResults = 10)
    {
        var messages = await _appClient.Users[_entraUserId].Messages.GetAsync((config) =>
        {
            // Request specific properties.
            config.QueryParameters.Select = ["subject", "from", "receivedDateTime", "bodyPreview", "isRead"];
            // Get most recent emails.
            config.QueryParameters.Top = maxResults;
            // Sort by received date, most recent first.
            config.QueryParameters.Orderby = ["receivedDateTime desc"];
        });

        if (messages?.Value == null)
            return [];

        return messages.Value
            .Select(m => new EmailEntry
            {
                Subject = m.Subject ?? string.Empty,
                From = m.From?.EmailAddress?.Address ?? string.Empty,
                ReceivedDateTime = m.ReceivedDateTime ?? DateTimeOffset.MinValue,
                BodyPreview = m.BodyPreview ?? string.Empty,
                IsRead = m.IsRead ?? false
            }).ToArray();
    }

    public async Task<ContactEntry[]> GetUserContactsAsync(int maxResults = 50)
    {
        var contacts = await _appClient.Users[_entraUserId].Contacts.GetAsync((config) =>
        {
            // Request specific properties.
            config.QueryParameters.Select = ["displayName", "emailAddresses", "mobilePhone", "businessPhones", "companyName", "jobTitle"];
            // Get contacts up to the specified limit.
            config.QueryParameters.Top = maxResults;
            // Sort by display name.
            config.QueryParameters.Orderby = ["displayName"];
        });

        if (contacts?.Value == null)
            return [];

        return contacts.Value
            .Select(c => new ContactEntry
            {
                DisplayName = c.DisplayName ?? string.Empty,
                EmailAddresses = c.EmailAddresses?.Select(e => e.Address ?? string.Empty).Where(e => !string.IsNullOrEmpty(e)).ToArray() ?? [],
                MobilePhone = c.MobilePhone ?? string.Empty,
                BusinessPhones = c.BusinessPhones?.Where(p => !string.IsNullOrEmpty(p)).ToArray() ?? [],
                CompanyName = c.CompanyName ?? string.Empty,
                JobTitle = c.JobTitle ?? string.Empty
            }).ToArray();
    }

    public async Task<ContactEntry?> GetUserContactByNameAsync(string name)
    {
        var searchTerm = name.Trim();
        
        var contacts = await _appClient.Users[_entraUserId].Contacts.GetAsync((config) =>
        {
            // Request specific properties.
            config.QueryParameters.Select = ["displayName", "emailAddresses", "mobilePhone", "businessPhones", "companyName", "jobTitle", "givenName", "surname"];
            // Filter by display name, given name, or surname containing the search term.
            config.QueryParameters.Filter = $"startswith(displayName,'{searchTerm}') or startswith(givenName,'{searchTerm}') or startswith(surname,'{searchTerm}')";
            // Get top 10 matches.
            config.QueryParameters.Top = 10;
        });

        if (contacts?.Value == null || contacts.Value.Count == 0)
            return null;

        // Return the first match, or the best match if display name contains the exact search term.
        var exactMatch = contacts.Value.FirstOrDefault(c => 
            c.DisplayName?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) == true);
        
        var contact = exactMatch ?? contacts.Value.First();

        return new ContactEntry
        {
            DisplayName = contact.DisplayName ?? string.Empty,
            EmailAddresses = contact.EmailAddresses?.Select(e => e.Address ?? string.Empty).Where(e => !string.IsNullOrEmpty(e)).ToArray() ?? [],
            MobilePhone = contact.MobilePhone ?? string.Empty,
            BusinessPhones = contact.BusinessPhones?.Where(p => !string.IsNullOrEmpty(p)).ToArray() ?? [],
            CompanyName = contact.CompanyName ?? string.Empty,
            JobTitle = contact.JobTitle ?? string.Empty
        };
    }

    public async Task<string> SendEmailAsync(string toAddress, string subject, string body)
    {
        var message = new Message
        {
            Subject = subject,
            Body = new ItemBody
            {
                ContentType = BodyType.Text,
                Content = body
            },
            ToRecipients =
            [
                new Recipient
                {
                    EmailAddress = new EmailAddress
                    {
                        Address = toAddress
                    }
                }
            ]
        };

        await _appClient.Users[_entraUserId].SendMail.PostAsync(new Microsoft.Graph.Users.Item.SendMail.SendMailPostRequestBody
        {
            Message = message,
            SaveToSentItems = true
        });

        return $"Email sent successfully to {toAddress}";
    }
}

public class EventEntry
{
    public required string Description { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
}

public class EmailEntry
{
    public required string Subject { get; init; }
    public required string From { get; init; }
    public required DateTimeOffset ReceivedDateTime { get; init; }
    public required string BodyPreview { get; init; }
    public required bool IsRead { get; init; }
}

public class ContactEntry
{
    public required string DisplayName { get; init; }
    public required string[] EmailAddresses { get; init; }
    public required string MobilePhone { get; init; }
    public required string[] BusinessPhones { get; init; }
    public required string CompanyName { get; init; }
    public required string JobTitle { get; init; }
}
