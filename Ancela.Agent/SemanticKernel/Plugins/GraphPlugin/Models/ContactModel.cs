namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

public class ContactModel
{
    public required string DisplayName { get; init; }
    public required string[] EmailAddresses { get; init; }

    /// <summary>Every number the contact has, each with its label ("mobile", "home", "work", or the owner's own).</summary>
    public required ContactPhone[] Phones { get; init; }

    public required string CompanyName { get; init; }
    public required string JobTitle { get; init; }
}

public record ContactPhone(string Label, string Number);
