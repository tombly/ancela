namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

public class SavedEmailModel
{
    public required string FileName { get; init; }
    public required string WebUrl { get; init; }

    /// <summary>
    /// True when the whole message was over Graph's 2 MB conversion limit, so the PDF was rendered
    /// from the headers and body alone.
    /// </summary>
    public required bool BodyOnly { get; init; }

    /// <summary>In body-only mode, the attachments that are not in the PDF. Empty otherwise.</summary>
    public required string[] OmittedAttachments { get; init; }
}
