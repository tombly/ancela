using System.ComponentModel;
using Ancela.Agent.SemanticKernel.Plugins.MemoryPlugin;
using ModelContextProtocol.Server;

namespace Ancela.McpServer;

/// <summary>
/// The tools Claude Desktop can call. Read-only on purpose, and deliberately narrow.
///
/// This is a second door into Ancela's data: it talks to Cosmos directly, so it does not inherit
/// <c>KernelProfilePolicy</c> or <c>AutonomousToolGuardFilter</c>, which only govern calls routed
/// through the kernel. Every tool added here therefore has to be safe on its own terms rather than
/// by virtue of the agent's guards — which is why the set stays at shared to-dos and knowledge
/// (neither is owner-only) and why <c>McpToolSurfaceTests</c> pins it.
/// </summary>
[McpServerToolType]
public sealed class MemoryTools(IMemoryClient _memory, AncelaEnvironment _environment)
{
    [McpServerTool(Name = "get_todos", ReadOnly = true)]
    [Description("Lists Ancela's shared to-do items. These are the same to-dos the assistant reads and writes over SMS.")]
    public async Task<IReadOnlyList<TodoView>> GetTodosAsync()
    {
        var todos = await _memory.GetToDosAsync(_environment.AgentPhoneNumber);
        return [.. todos.Select(t => new TodoView(t.Id, t.Content, t.Created))];
    }

    [McpServerTool(Name = "get_knowledge", ReadOnly = true)]
    [Description("Lists Ancela's shared knowledge notes — durable facts the assistant has been told to remember.")]
    public async Task<IReadOnlyList<KnowledgeView>> GetKnowledgeAsync()
    {
        var entries = await _memory.GetKnowledgeAsync(_environment.AgentPhoneNumber);
        return [.. entries.Select(k => new KnowledgeView(k.Id, k.Content, k.Created))];
    }
}

/// <summary>
/// Projections rather than the Cosmos models. They drop <c>Deleted</c> (the reads already exclude
/// soft-deleted rows, so exposing it only invites confusion) and, for knowledge,
/// <c>UserPhoneNumber</c> — who added a note is not something the model needs, and phone numbers
/// are the one piece of personal data in these containers.
/// </summary>
public record TodoView(Guid Id, string Content, DateTimeOffset Created);

public record KnowledgeView(Guid Id, string Content, DateTimeOffset Created);
