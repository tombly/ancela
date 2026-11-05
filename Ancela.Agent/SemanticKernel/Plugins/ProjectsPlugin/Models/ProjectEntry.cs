using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Ancela.Agent.SemanticKernel.Plugins.ProjectsPlugin.Models;

/// <summary>
/// A trackable item embedded within a <see cref="Project"/>, e.g. an account to
/// verify or an item to sell. Intentionally minimal — free-form <see cref="Status"/>
/// and <see cref="Category"/> labels, a soft-delete flag and who added it, with no
/// timestamps (only the parent project is timestamped).
/// </summary>
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public class ProjectEntry
{
    public Guid Id { get; set; }
    public required string Content { get; set; }
    public string? Status { get; set; }    // free label: "open"/"done"/"sold"/…
    public string? Category { get; set; }  // free label grouping entries into named lists within a project: "packing"/"gear"/…
    public bool Deleted { get; set; }       // soft-delete flag

    /// <summary>
    /// Who added it, so a read of another user's entry counts as outside text. Null on entries
    /// added before 2026-11, which count as the project creator's: until then no other user had
    /// added one. Stored (Newtonsoft) but left out of what the model is shown (System.Text.Json).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? AddedBy { get; set; }
}
