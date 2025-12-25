using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace Ancilla.FunctionApp.Services;

public interface IGraphService
{
    Task<EventEntry[]> GetUserEventsAsync(DateTimeOffset start, DateTimeOffset end);
}

/// <summary>
/// Service for interacting with Microsoft Graph API.
/// </summary>
public class GraphService : IGraphService
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

    public GraphService()
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
}

public class EventEntry
{
    public required string Description { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
}