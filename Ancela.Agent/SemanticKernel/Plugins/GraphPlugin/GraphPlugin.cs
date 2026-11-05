using System.ComponentModel;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

/// <summary>
/// Calendar, contacts and email. Email goes through <paramref name="mailClient"/>: Microsoft 365 or
/// iCloud, as MAIL_PROVIDER says (DependencyModule); the calendar and contacts are still Graph's.
/// </summary>
public class GraphPlugin(IGraphClient graphClient, IMailClient mailClient)
{
    #region Calendar

    [KernelFunction("get_calendar_events")]
    [Tool(Grant = ToolGrant.Calendar, Autonomous = Autonomy.ScheduledTask, Reads = UntrustedSource.MailOrCalendar)]
    [Description("Retrieves calendar events for the user within the specified date range.")]
    public async Task<EventModel[]> GetCalendarEventsAsync(
         [Description("The start date and time of the range to query")]
         DateTimeOffset start,
         [Description("The end date and time of the range to query")]
         DateTimeOffset end)
    {
        return await graphClient.GetUserEventsAsync(start, end);
    }

    [KernelFunction("create_calendar_event")]
    [Tool]
    [Description("Creates a new calendar event for the user with the specified details.")]
    public async Task<EventModel> CreateCalendarEventAsync(
         [Description("The subject or title of the event.")]
         string subject,
         [Description("The start date of the event 'yyyy-MM-ddTHH:mm:ss'.")]
         string start,
         [Description("The end date of the event 'yyyy-MM-ddTHH:mm:ss'.")]
         string end,
         [Description("The location of the event (optional).")]
         string? location = null,
         [Description("The body or description of the event (optional).")]
         string? body = null,
         [Description("Whether this is an all-day event (optional, defaults to false).")]
         bool isAllDay = false)
    {
        return await graphClient.CreateEventAsync(subject, start, end, location, body, isAllDay);
    }

    #endregion

    #region Email

    [KernelFunction("get_recent_emails")]
    [Tool(Autonomous = Autonomy.ScheduledTask, Reads = UntrustedSource.MailOrCalendar)]
    [Description("Retrieves emails from one of the user's mail folders, newest first, at most 50. Reads the inbox unless the user explicitly asks for sent or deleted mail. Without a search the 50 newest may cover only the last day or two, so to find a particular email, pass search.")]
    public async Task<EmailModel[]> GetRecentEmailsAsync(
         [Description("Which mail folder to read: Inbox, Sent, or Deleted. Defaults to Inbox.")]
         MailFolder folder = MailFolder.Inbox,
         [Description("Finds particular emails anywhere in the folder: a sender's address or name, or words from the subject or body, e.g. 'from:someone@example.com' or 'invoice march'.")]
         string? search = null)
    {
        return await mailClient.GetUserEmailsAsync(folder, search);
    }

    [KernelFunction("read_email")]
    [Tool(Autonomous = Autonomy.ScheduledTask, Reads = UntrustedSource.MailOrCalendar, Email = OneEmail.Reads)]
    [Description("Reads one email in full: sender, recipients, date, the whole body as text and the names of its attachments. get_recent_emails shows only the first few lines of each email, so use this to find out what an email says.")]
    public async Task<EmailContentModel> ReadEmailAsync(
         [Description("The email's id, exactly as returned by get_recent_emails.")]
         string emailId)
    {
        return await mailClient.GetEmailAsync(emailId);
    }

    [KernelFunction("read_attachment")]
    [Tool(Autonomous = Autonomy.ScheduledTask, Reads = UntrustedSource.MailOrCalendar, Email = OneEmail.Reads)]
    [Description("Reads a text file attached to an email (.txt, .csv, .xml, .json, .ics and the like), unpacking a .zip or .gz first. Doesn't read PDFs, images or Office documents. read_email lists an email's attachments.")]
    public async Task<AttachmentContentModel> ReadAttachmentAsync(
         [Description("The email's id, exactly as returned by get_recent_emails.")]
         string emailId,
         [Description("The attachment's file name, exactly as read_email lists it.")]
         string name)
    {
        return await mailClient.GetAttachmentAsync(emailId, name);
    }

    [KernelFunction("mark_email_read")]
    [Tool(Autonomous = Autonomy.ScheduledTask, Email = OneEmail.Changes)]
    [Description("Marks one email as read, or as unread. Only when the user or the task asks for it.")]
    public async Task<string> MarkEmailReadAsync(
         [Description("The email's id, exactly as returned by get_recent_emails.")]
         string emailId,
         [Description("True to mark it read, false to mark it unread.")]
         bool read = true)
    {
        return await mailClient.MarkReadAsync(emailId, read);
    }

    [KernelFunction("trash_email")]
    [Tool(Autonomous = Autonomy.ScheduledTask, Email = OneEmail.Changes)]
    [Description("Moves one email to Deleted Items, where it can still be read and restored. Only when the user or the task asks for it. The move gives the email a new id, so do anything else to it first, such as marking it read.")]
    public async Task<string> TrashEmailAsync(
         [Description("The email's id, exactly as returned by get_recent_emails.")]
         string emailId)
    {
        return await mailClient.MoveToDeletedItemsAsync(emailId);
    }

    [KernelFunction("send_email")]
    [Tool(OffAfter = UntrustedSource.Any)]
    [Description("Sends an email to the specified recipient.")]
    public async Task<string> SendEmailAsync(
         [Description("The email address of the recipient.")]
         string toAddress,
         [Description("The subject line of the email.")]
         string subject,
         [Description("The body content of the email.")]
         string body)
    {
        return await mailClient.SendEmailAsync(toAddress, subject, body);
    }

    #endregion

    #region Contacts

    [KernelFunction("get_contacts")]
    [Tool(Grant = ToolGrant.Contacts, Autonomous = Autonomy.Both)]
    [Description("Retrieves the user's contacts from their address book: names, email addresses and phone numbers. What the user asked you to remember about someone is saved knowledge (get_knowledge), not a contact.")]
    public async Task<ContactModel[]> GetContactsAsync(
         [Description("The maximum number of contacts to retrieve.")]
         int maxResults = 100)
    {
        return await graphClient.GetUserContactsAsync(maxResults);
    }

    [KernelFunction("get_contact_by_name")]
    [Tool(Grant = ToolGrant.Contacts, Autonomous = Autonomy.Both)]
    [Description("Searches for and retrieves a specific contact by their name (searches display name, first name, and last name).")]
    public async Task<ContactModel?> GetContactByNameAsync(
         [Description("The name to search for.")]
         string name)
    {
        return await graphClient.GetUserContactByNameAsync(name);
    }

    #endregion
}
