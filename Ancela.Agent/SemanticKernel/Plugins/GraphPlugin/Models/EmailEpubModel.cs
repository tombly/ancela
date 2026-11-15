namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

/// <summary>A whole email as an EPUB for the reMarkable (<see cref="EmailEpub"/>), before it is sent.</summary>
public class EmailEpubModel
{
    public required string? Subject { get; init; }
    public required byte[] Epub { get; init; }

    /// <summary>Images the email links to on other servers, whether or not they were downloaded.</summary>
    public required int LinkedImages { get; init; }

    /// <summary>
    /// Images that should be in the book and aren't: embedded ones, or linked ones the owner said yes
    /// to, that couldn't be downloaded, were of a kind an EPUB can't hold, or were too large.
    /// </summary>
    public required int ImagesMissing { get; init; }
}
