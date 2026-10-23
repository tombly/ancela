namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

public class SavedEmailModel
{
    /// <summary>False when nothing was saved yet: see <see cref="Note"/>.</summary>
    public required bool Saved { get; init; }

    public string? FileName { get; init; }
    public string? WebUrl { get; init; }

    /// <summary>Images the email links to on other servers.</summary>
    public required int LinkedImages { get; init; }

    /// <summary>Images that should be in the PDF and aren't.</summary>
    public int ImagesMissing { get; init; }

    /// <summary>When nothing was saved, what to ask the user.</summary>
    public string? Note { get; init; }
}
