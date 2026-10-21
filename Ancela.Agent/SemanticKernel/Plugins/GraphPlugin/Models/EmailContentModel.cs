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

    /// <summary>Attachments are not read; this says whether there are any the body may refer to.</summary>
    public required bool HasAttachments { get; init; }
}
