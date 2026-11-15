namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

/// <summary>
/// The owner's mail, as the email tools use it: iCloud over IMAP and SMTP (<see cref="ImapMailClient"/>).
/// It reads only the Inbox, Sent and Deleted folders, gives each email an id the other calls take back,
/// and names the email it changed in the result, since the audit log records results and a move
/// changes an email's id.
/// </summary>
public interface IMailClient
{
    Task<EmailModel[]> GetUserEmailsAsync(MailFolder folder = MailFolder.Inbox, string? search = null);
    Task<EmailContentModel> GetEmailAsync(string messageId);
    Task<AttachmentContentModel> GetAttachmentAsync(string messageId, string attachmentName);
    Task<string> MarkReadAsync(string messageId, bool read);
    Task<string> MoveToDeletedItemsAsync(string messageId);
    Task<string> SendEmailAsync(string toAddress, string subject, string body);
    Task<int> GetLinkedImageCountAsync(string messageId);
    Task<EmailEpubModel> RenderEmailAsEpubAsync(string messageId, TimeZoneInfo timeZone, bool includeLinkedImages);
}
