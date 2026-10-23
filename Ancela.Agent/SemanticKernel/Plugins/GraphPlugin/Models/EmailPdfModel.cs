namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

/// <summary>A whole email rendered as a PDF by Graph, before it is saved or sent anywhere.</summary>
public class EmailPdfModel
{
    public required string? Subject { get; init; }
    public required DateTimeOffset ReceivedDateTime { get; init; }
    public required byte[] Pdf { get; init; }

    /// <summary>Images the email links to on other servers, whether or not they were downloaded.</summary>
    public required int LinkedImages { get; init; }

    /// <summary>
    /// Images that should be in the PDF and aren't: embedded ones, or linked ones the owner said yes
    /// to, that couldn't be downloaded or didn't fit Graph's 2 MB conversion limit.
    /// </summary>
    public required int ImagesMissing { get; init; }
}
