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
}
