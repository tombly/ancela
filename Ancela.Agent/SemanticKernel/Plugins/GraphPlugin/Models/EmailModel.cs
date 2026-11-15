namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

public class EmailModel
{
    /// <summary>The email's id, its folder and IMAP UID (<see cref="ImapSearch.EmailId"/>); how <c>read_email</c> and the other one-email tools refer back to this email.</summary>
    public required string Id { get; init; }
    public required string Subject { get; init; }
    public required string From { get; init; }
    public required DateTimeOffset ReceivedDateTime { get; init; }
    public required string BodyPreview { get; init; }
    public required bool IsRead { get; init; }
}
