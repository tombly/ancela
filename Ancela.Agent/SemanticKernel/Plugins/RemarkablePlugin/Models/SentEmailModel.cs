namespace Ancela.Agent.SemanticKernel.Plugins.RemarkablePlugin;

public class SentEmailModel
{
    /// <summary>False when nothing was sent yet: see <see cref="Note"/>.</summary>
    public required bool Sent { get; init; }

    /// <summary>The document's name on the tablet: the email's subject.</summary>
    public string? Title { get; init; }

    /// <summary>Images the email links to on other servers.</summary>
    public required int LinkedImages { get; init; }

    /// <summary>Images that should be in the PDF and aren't.</summary>
    public int ImagesMissing { get; init; }

    /// <summary>When nothing was sent, what to ask the user.</summary>
    public string? Note { get; init; }
}
