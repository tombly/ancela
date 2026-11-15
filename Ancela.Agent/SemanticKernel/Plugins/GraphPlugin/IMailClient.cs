namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

/// <summary>
/// The owner's mail, as the email tools use it: Microsoft 365 through Graph (<see cref="GraphClient"/>)
/// or iCloud over IMAP and SMTP (<see cref="ImapMailClient"/>). Both read only the Inbox, Sent and
/// Deleted folders, give each email an id the other calls take back, and name the email they changed
/// in the result, since the audit log records results and a move changes an email's id.
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

/// <summary>
/// Turns one HTML page into a PDF, in the OneDrive folder (<see cref="GraphClient"/>). Emails go to
/// the reMarkable as EPUBs now (<see cref="EmailEpub"/>); this goes with the rest of Graph's mail.
/// </summary>
public interface IPdfConverter
{
    Task<byte[]> ConvertHtmlToPdfAsync(byte[] page);
}
