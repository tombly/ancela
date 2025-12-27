namespace Ancela.Agent.SemanticKernel.Plugins.MemoryPlugin.Models;

public class KnowledgeModel
{
    public Guid Id { get; set; }
    public required string Content { get; set; }
    public required string UserPhoneNumber { get; set; }
    public required DateTimeOffset Created { get; set; }
    public required DateTimeOffset? Deleted { get; set; }
}