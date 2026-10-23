namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

/// <summary>A whole email rendered as a PDF by Graph, before it is saved or sent anywhere.</summary>
public class EmailPdfModel
{
    public required string? Subject { get; init; }
    public required DateTimeOffset ReceivedDateTime { get; init; }
    public required byte[] Pdf { get; init; }

    /// <summary>
    /// True when the whole message was over Graph's 2 MB conversion limit, so the PDF was rendered
    /// from the headers and body alone.
    /// </summary>
    public required bool BodyOnly { get; init; }

    /// <summary>In body-only mode, the attachments that are not in the PDF. Empty otherwise.</summary>
    public required string[] OmittedAttachments { get; init; }
}
