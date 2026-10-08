using System.Reflection;
using Ancela.Agent.SemanticKernel.Plugins.MemoryPlugin;
using Ancela.Agent.SemanticKernel.Plugins.MemoryPlugin.Models;
using Ancela.McpServer;
using FluentAssertions;
using ModelContextProtocol.Server;
using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// Pins what the MCP server exposes.
///
/// The server reads Cosmos directly, so it does not inherit `KernelProfilePolicy` or
/// `AutonomousToolGuardFilter` — those only govern calls routed through the kernel. A tool added
/// here is therefore reachable by anything holding the stdio pipe, with none of the agent's
/// gating. These tests exist so widening that surface has to be deliberate.
/// </summary>
public class McpToolSurfaceTests
{
    private const string Agent = "+10000000000";

    // Every tool the server is allowed to expose. Adding one here is a decision to be made on the
    // tool's own merits — see the class comment.
    private static readonly string[] AllowedTools = ["get_todos", "get_knowledge"];

    private static MethodInfo[] ToolMethods() =>
        [.. typeof(MemoryTools).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)];

    [Fact]
    public void TheServerExposesExactlyTheAllowedTools()
    {
        var names = ToolMethods()
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name)
            .ToArray();

        names.Should().BeEquivalentTo(AllowedTools);
    }

    [Fact]
    public void EveryToolIsMarkedReadOnly()
    {
        // The server is a viewer. A write would also bypass the SMS notification MemoryPlugin
        // sends the owner when someone else adds a to-do.
        foreach (var method in ToolMethods())
        {
            var attribute = method.GetCustomAttribute<McpServerToolAttribute>()!;
            attribute.ReadOnly.Should().BeTrue(because: $"{attribute.Name} must not mutate Ancela's data");
        }
    }

    [Fact]
    public void KnowledgeProjection_OmitsWhoAddedTheNote()
    {
        // KnowledgeModel carries UserPhoneNumber; phone numbers are the one piece of personal data
        // in these containers and the model has no use for them.
        typeof(KnowledgeView).GetProperties().Select(p => p.Name)
            .Should().NotContain("UserPhoneNumber");
    }

    [Fact]
    public void Projections_OmitTheSoftDeleteMarker()
    {
        // The reads already exclude soft-deleted rows, so surfacing Deleted only invites confusion.
        typeof(TodoView).GetProperties().Select(p => p.Name).Should().NotContain("Deleted");
        typeof(KnowledgeView).GetProperties().Select(p => p.Name).Should().NotContain("Deleted");
    }

    [Fact]
    public async Task GetTodos_PartitionsByTheConfiguredAgentNumber_AndMapsContent()
    {
        var memory = new Mock<IMemoryClient>();
        memory.Setup(m => m.GetToDosAsync(Agent)).ReturnsAsync([
            new ToDoModel { Id = Guid.NewGuid(), Content = "Replace the furnace filter", Created = DateTimeOffset.UnixEpoch, Deleted = null },
        ]);

        var tools = new MemoryTools(memory.Object, EnvironmentFor(Agent));
        var todos = await tools.GetTodosAsync();

        todos.Should().ContainSingle().Which.Content.Should().Be("Replace the furnace filter");
        memory.Verify(m => m.GetToDosAsync(Agent), Times.Once);
    }

    [Fact]
    public async Task GetKnowledge_MapsContent()
    {
        var memory = new Mock<IMemoryClient>();
        memory.Setup(m => m.GetKnowledgeAsync(Agent)).ReturnsAsync([
            new KnowledgeModel
            {
                Id = Guid.NewGuid(), Content = "The spare key is in the blue tin",
                UserPhoneNumber = "+15551234567", Created = DateTimeOffset.UnixEpoch, Deleted = null,
            },
        ]);

        var tools = new MemoryTools(memory.Object, EnvironmentFor(Agent));
        var entries = await tools.GetKnowledgeAsync();

        entries.Should().ContainSingle().Which.Content.Should().Be("The spare key is in the blue tin");
    }

    [Fact]
    public void MissingAgentNumber_FailsLoudly()
    {
        // The agent number is the partition key: without it every read would silently return
        // nothing, which looks like an empty to-do list rather than a misconfiguration.
        var previousAgent = Environment.GetEnvironmentVariable("ANCELA_AGENT_PHONE_NUMBER");
        var previousTwilio = Environment.GetEnvironmentVariable("TWILIO_PHONE_NUMBER");
        try
        {
            Environment.SetEnvironmentVariable("ANCELA_AGENT_PHONE_NUMBER", null);
            Environment.SetEnvironmentVariable("TWILIO_PHONE_NUMBER", null);

            var act = () => new AncelaEnvironment().AgentPhoneNumber;

            act.Should().Throw<InvalidOperationException>().WithMessage("*ANCELA_AGENT_PHONE_NUMBER*");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANCELA_AGENT_PHONE_NUMBER", previousAgent);
            Environment.SetEnvironmentVariable("TWILIO_PHONE_NUMBER", previousTwilio);
        }
    }

    private static AncelaEnvironment EnvironmentFor(string agentPhoneNumber)
    {
        Environment.SetEnvironmentVariable("ANCELA_AGENT_PHONE_NUMBER", agentPhoneNumber);
        return new AncelaEnvironment();
    }
}
