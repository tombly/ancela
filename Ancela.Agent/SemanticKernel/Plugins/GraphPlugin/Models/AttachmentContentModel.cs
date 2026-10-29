namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

/// <summary>An email attachment's text, as read_attachment returns it.</summary>
public class AttachmentContentModel
{
    /// <summary>The attachment's file name, as read_email lists it.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The text files in it: the attachment itself, or for a .zip each text file inside, together at
    /// most <see cref="AttachmentText.TextCap"/> characters.
    /// </summary>
    public required AttachmentFileModel[] Files { get; init; }

    /// <summary>Files in a .zip that were not read, each with the reason.</summary>
    public required string[] NotRead { get; init; }
}

/// <summary>One text file from an attachment.</summary>
public class AttachmentFileModel
{
    public required string Name { get; init; }
    public required string Text { get; init; }

    /// <summary>True when the file was longer than the limit and the rest is missing.</summary>
    public required bool Truncated { get; init; }
}
