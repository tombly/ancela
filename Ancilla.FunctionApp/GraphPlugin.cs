using System.ComponentModel;
using Ancilla.FunctionApp.Services;
using Microsoft.SemanticKernel;

namespace Ancilla.FunctionApp;

public class GraphPlugin(IGraphService graphService)
{
    [KernelFunction("get_calendar_events")]
    [Description("Retrieves calendar events for the user within the specified date range.")]
    public async Task<EventEntry[]> GetCalendarEventsAsync(
         [Description("The start date and time of the range to query")] 
         DateTimeOffset start, 
         [Description("The end date and time of the range to query")]
         DateTimeOffset end)
    {
        return await graphService.GetUserEventsAsync(start, end);
    }

    [KernelFunction("get_recent_emails")]
    [Description("Retrieves the most recent emails for the user.")]
    public async Task<EmailEntry[]> GetRecentEmailsAsync(
         [Description("The maximum number of emails to retrieve (default: 10)")] 
         int maxResults = 10)
    {
        return await graphService.GetUserEmailsAsync(maxResults);
    }

    [KernelFunction("get_contacts")]
    [Description("Retrieves the user's contacts from their address book.")]
    public async Task<ContactEntry[]> GetContactsAsync(
         [Description("The maximum number of contacts to retrieve (default: 50)")] 
         int maxResults = 50)
    {
        return await graphService.GetUserContactsAsync(maxResults);
    }

    [KernelFunction("get_contact_by_name")]
    [Description("Searches for and retrieves a specific contact by their name (searches display name, first name, and last name).")]
    public async Task<ContactEntry?> GetContactByNameAsync(
         [Description("The name to search for")] 
         string name)
    {
        return await graphService.GetUserContactByNameAsync(name);
    }

    [KernelFunction("send_email")]
    [Description("Sends an email to the specified recipient.")]
    public async Task<string> SendEmailAsync(
         [Description("The email address of the recipient")] 
         string toAddress,
         [Description("The subject line of the email")] 
         string subject,
         [Description("The body content of the email")] 
         string body)
    {
        return await graphService.SendEmailAsync(toAddress, subject, body);
    }
}