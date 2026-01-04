using Ancela.Agent.SemanticKernel.Plugins.ChatPlugin;
using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using Ancela.Agent.SemanticKernel.Plugins.MemoryPlugin;
using Ancela.Agent.SemanticKernel.Plugins.MemoryPlugin.Models;
using Ancela.Agent.SemanticKernel.Plugins.YnabPlugin;
using Ancela.Agent.Services;
using Microsoft.Extensions.Configuration;
using Moq;
using OpenAI;

namespace Ancela.Agent.Tests;

/// <summary>
/// Base class for agent integration tests that use real OpenAI calls
/// with mocked data services to verify function call behavior.
/// </summary>
public abstract class AgentTestBase
{
    // Test phone numbers
    protected const string AgentPhoneNumber = "+15551234567";
    protected const string UserPhoneNumber = "+15559876543";

    // Real OpenAI client
    protected readonly OpenAIClient OpenAIClient;

    // Mocked data services
    protected readonly Mock<IHistoryService> MockHistoryService;
    protected readonly Mock<IMemoryClient> MockMemoryClient;
    protected readonly Mock<IGraphClient> MockGraphClient;
    protected readonly Mock<ILoopbackService> MockLoopbackService;

    // System under test
    protected readonly Agent Agent;

    // Test session
    protected readonly SessionEntry TestSession;

    protected AgentTestBase()
    {
        // Load configuration from user secrets
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<AgentTestBase>()
            .AddEnvironmentVariables()
            .Build();

        var apiKey = configuration["Parameters:openai-api-key"]
            ?? configuration["OpenAI:ApiKey"]
            ?? configuration["OPENAI_API_KEY"]
            ?? throw new InvalidOperationException(
                "OpenAI API key not found. Set it via user secrets (Parameters:openai-api-key) or environment variable (OPENAI_API_KEY).");

        // Create real OpenAI client
        OpenAIClient = new OpenAIClient(apiKey);

        // Setup mocked services with default behaviors
        MockHistoryService = new Mock<IHistoryService>();
        MockMemoryClient = new Mock<IMemoryClient>();
        MockGraphClient = new Mock<IGraphClient>();
        MockLoopbackService = new Mock<ILoopbackService>();

        // Default: return empty history (fresh conversation)
        MockHistoryService
            .Setup(h => h.GetHistoryAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(Array.Empty<HistoryEntry>());

        // Default: return empty todos
        MockMemoryClient
            .Setup(m => m.GetToDosAsync(It.IsAny<string>()))
            .ReturnsAsync(Array.Empty<ToDoModel>());

        // Default: return empty knowledge
        MockMemoryClient
            .Setup(m => m.GetKnowledgeAsync(It.IsAny<string>()))
            .ReturnsAsync(Array.Empty<KnowledgeModel>());

        // Create plugins with mocked clients
        var memoryPlugin = new MemoryPlugin(MockMemoryClient.Object);
        var graphPlugin = new GraphPlugin(MockGraphClient.Object);
        var chatPlugin = new LoopbackPlugin(MockLoopbackService.Object);

        // YnabPlugin requires YnabClient. For tests, set a dummy token to avoid exceptions
        // The actual YNAB tests would need to mock the YnabClient or use integration testing
        Environment.SetEnvironmentVariable("YNAB_ACCESS_TOKEN", "test-token-for-testing");
        var ynabClient = new YnabClient();
        var ynabPlugin = new YnabPlugin(ynabClient);

        // Create agent with real OpenAI and mocked data services
        Agent = new Agent(
            OpenAIClient,
            MockHistoryService.Object,
            memoryPlugin,
            graphPlugin,
            ynabPlugin,
            chatPlugin);

        // Create test session
        TestSession = new SessionEntry
        {
            Id = Guid.NewGuid(),
            AgentPhoneNumber = AgentPhoneNumber,
            UserPhoneNumber = UserPhoneNumber,
            Created = DateTimeOffset.UtcNow,
            TimeZone = "Pacific Standard Time"
        };
    }

    /// <summary>
    /// Sends a message to the agent and returns the response.
    /// </summary>
    protected Task<string> SendMessageAsync(string message)
    {
        return Agent.Chat(message, UserPhoneNumber, AgentPhoneNumber, TestSession, []);
    }

    /// <summary>
    /// Configures the mock to return specific todos when GetToDosAsync is called.
    /// Useful for testing delete operations that need existing todo IDs.
    /// </summary>
    protected void SetupExistingTodos(params ToDoModel[] todos)
    {
        MockMemoryClient
            .Setup(m => m.GetToDosAsync(AgentPhoneNumber))
            .ReturnsAsync(todos);
    }

    /// <summary>
    /// Configures the mock to return specific knowledge entries when GetKnowledgeAsync is called.
    /// Useful for testing delete operations that need existing knowledge IDs.
    /// </summary>
    protected void SetupExistingKnowledge(params KnowledgeModel[] entries)
    {
        MockMemoryClient
            .Setup(m => m.GetKnowledgeAsync(AgentPhoneNumber))
            .ReturnsAsync(entries);
    }

    /// <summary>
    /// Creates a ToDoModel for testing.
    /// </summary>
    protected static ToDoModel CreateTodo(string content, Guid? id = null)
    {
        return new ToDoModel
        {
            Id = id ?? Guid.NewGuid(),
            Content = content,
            UserPhoneNumber = UserPhoneNumber,
            Created = DateTimeOffset.UtcNow,
            Deleted = null
        };
    }

    /// <summary>
    /// Creates a KnowledgeModel for testing.
    /// </summary>
    protected static KnowledgeModel CreateKnowledge(string content, Guid? id = null)
    {
        return new KnowledgeModel
        {
            Id = id ?? Guid.NewGuid(),
            Content = content,
            UserPhoneNumber = UserPhoneNumber,
            Created = DateTimeOffset.UtcNow,
            Deleted = null
        };
    }
}
