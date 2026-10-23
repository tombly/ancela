namespace Ancela.Agent.SemanticKernel.Plugins.RemarkablePlugin;

public class SentEmailModel
{
    /// <summary>The document's name on the tablet: the email's subject.</summary>
    public required string Title { get; init; }

    /// <summary>
    /// True when the whole message was over Graph's 2 MB conversion limit, so the PDF was rendered
    /// from the headers and body alone.
    /// </summary>
    public required bool BodyOnly { get; init; }

    /// <summary>In body-only mode, the attachments that are not in the PDF. Empty otherwise.</summary>
    public required string[] OmittedAttachments { get; init; }
}
