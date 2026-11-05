namespace Ancela.Agent.SemanticKernel.Plugins.ProjectsPlugin.Models;

/// <summary>
/// Lightweight projection returned by list_projects — no notes or entries, to keep
/// the model's token cost down. Use get_project for full detail.
/// </summary>
public class ProjectSummary
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? Purpose { get; set; }

    /// <summary>
    /// Who created it, so a read of another user's project counts as outside text. Read from
    /// Cosmos (Newtonsoft) but left out of what the model is shown (System.Text.Json).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? UserPhoneNumber { get; set; }
}
