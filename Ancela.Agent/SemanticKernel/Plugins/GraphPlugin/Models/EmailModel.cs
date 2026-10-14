namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

public class EmailModel
{
    /// <summary>Graph message id; how <c>save_email_to_onedrive</c> refers back to this email.</summary>
    public required string Id { get; init; }
    public required string Subject { get; init; }
    public required string From { get; init; }
    public required DateTimeOffset ReceivedDateTime { get; init; }
    public required string BodyPreview { get; init; }
    public required bool IsRead { get; init; }
}
