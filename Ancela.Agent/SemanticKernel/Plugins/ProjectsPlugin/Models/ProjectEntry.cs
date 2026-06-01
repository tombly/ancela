using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Ancela.Agent.SemanticKernel.Plugins.ProjectsPlugin.Models;

/// <summary>
/// A trackable item embedded within a <see cref="Project"/>, e.g. an account to
/// verify or an item to sell. Intentionally minimal — a free-form <see cref="Status"/>
/// label and a soft-delete flag, with no per-entry provenance or timestamps (only
/// the parent project is attributed and timestamped).
/// </summary>
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public class ProjectEntry
{
    public Guid Id { get; set; }
    public required string Content { get; set; }
    public string? Status { get; set; }   // free label: "open"/"done"/"sold"/…
    public bool Deleted { get; set; }      // soft-delete flag
}
