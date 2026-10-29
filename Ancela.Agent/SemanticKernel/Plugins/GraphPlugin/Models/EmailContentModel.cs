namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

/// <summary>One email in full, as read_email returns it.</summary>
public class EmailContentModel
{
    public required string Id { get; init; }
    public required string Subject { get; init; }
    public required string From { get; init; }
    public required string[] To { get; init; }
    public required string[] Cc { get; init; }
    public required DateTimeOffset ReceivedDateTime { get; init; }

    /// <summary>The body as text (Graph converts HTML), at most <see cref="GraphClient.EmailBodyCap"/> characters.</summary>
    public required string Body { get; init; }

    /// <summary>True when the body was longer than the cap and the rest is missing.</summary>
    public required bool Truncated { get; init; }

    /// <summary>
    /// The names of the files attached, pictures in the body aside. A report sent as a file is only
    /// in its attachment, so read_attachment reads the text ones by these names.
    /// </summary>
    public required string[] Attachments { get; init; }
}
