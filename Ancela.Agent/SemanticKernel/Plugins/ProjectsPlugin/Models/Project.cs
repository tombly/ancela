using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Ancela.Agent.SemanticKernel.Plugins.ProjectsPlugin.Models;

/// <summary>
/// A project: a workspace for a larger or longer-lived effort. Holds a freeform
/// notes body the agent curates plus its trackable entries, embedded inline.
/// Shared across the instance's authorized users and partitioned by the agent's
/// phone number.
/// </summary>
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public class Project
{
    public Guid Id { get; set; }
    public required string AgentPhoneNumber { get; set; }   // partition key
    public required string UserPhoneNumber { get; set; }    // provenance: creator
    public required string Name { get; set; }
    public string? Purpose { get; set; }
    public string Notes { get; set; } = "";                 // freeform markdown workspace
    public bool IsArchived { get; set; }
    public List<ProjectEntry> Entries { get; set; } = [];   // embedded trackable items
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
