using Ancilla.FunctionApp.Services;
using Microsoft.Extensions.Configuration;
using Moq;
using OpenAI;

namespace Ancilla.FunctionApp.Tests;

/// <summary>
/// Base class for ChatService integration tests that use real OpenAI calls
/// with mocked data services to verify function call behavior.
/// </summary>
public abstract class ChatServiceTestBase
{
    // Test phone numbers
    protected const string AgentPhoneNumber = "+15551234567";
    protected const string UserPhoneNumber = "+15559876543";

    // Real OpenAI client
    protected readonly OpenAIClient OpenAIClient;

    // Mocked data services
    protected readonly Mock<IHistoryService> MockHistoryService;
    protected readonly Mock<ITodoService> MockTodoService;
    protected readonly Mock<IKnowledgeService> MockKnowledgeService;
    protected readonly Mock<IGraphService> MockGraphService;

    // System under test
    protected readonly ChatService ChatService;

    // Test session
    protected readonly SessionEntry TestSession;

    protected ChatServiceTestBase()
    {
        // Load configuration from user secrets
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<ChatServiceTestBase>()
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
        MockTodoService = new Mock<ITodoService>();
        MockKnowledgeService = new Mock<IKnowledgeService>();
        MockGraphService = new Mock<IGraphService>();

        // Default: return empty history (fresh conversation)
        MockHistoryService
            .Setup(h => h.GetHistoryAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(Array.Empty<HistoryEntry>());

        // Default: return empty todos
        MockTodoService
            .Setup(t => t.GetTodosAsync(It.IsAny<string>()))
            .ReturnsAsync(Array.Empty<TodoEntry>());

        // Default: return empty knowledge
        MockKnowledgeService
            .Setup(k => k.GetKnowledgeAsync(It.IsAny<string>()))
            .ReturnsAsync(Array.Empty<KnowledgeEntry>());

        // Create ChatService with real OpenAI and mocked data services
        ChatService = new ChatService(
            OpenAIClient,
            MockTodoService.Object,
            MockKnowledgeService.Object,
            MockHistoryService.Object,
            MockGraphService.Object);

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
    /// Sends a message to the ChatService and returns the response.
    /// </summary>
    protected Task<string> SendMessageAsync(string message)
    {
        return ChatService.Chat(message, UserPhoneNumber, AgentPhoneNumber, TestSession);
    }

    /// <summary>
    /// Configures the mock to return specific todos when GetTodosAsync is called.
    /// Useful for testing delete operations that need existing todo IDs.
    /// </summary>
    protected void SetupExistingTodos(params TodoEntry[] todos)
    {
        MockTodoService
            .Setup(t => t.GetTodosAsync(AgentPhoneNumber))
            .ReturnsAsync(todos);
    }

    /// <summary>
    /// Configures the mock to return specific knowledge entries when GetKnowledgeAsync is called.
    /// Useful for testing delete operations that need existing knowledge IDs.
    /// </summary>
    protected void SetupExistingKnowledge(params KnowledgeEntry[] entries)
    {
        MockKnowledgeService
            .Setup(k => k.GetKnowledgeAsync(AgentPhoneNumber))
            .ReturnsAsync(entries);
    }

    /// <summary>
    /// Creates a TodoEntry for testing.
    /// </summary>
    protected static TodoEntry CreateTodo(string content, Guid? id = null)
    {
        return new TodoEntry
        {
            Id = id ?? Guid.NewGuid(),
            Content = content,
            UserPhoneNumber = UserPhoneNumber,
            Created = DateTimeOffset.UtcNow,
            Deleted = null
        };
    }

    /// <summary>
    /// Creates a KnowledgeEntry for testing.
    /// </summary>
    protected static KnowledgeEntry CreateKnowledge(string content, Guid? id = null)
    {
        return new KnowledgeEntry
        {
            Id = id ?? Guid.NewGuid(),
            Content = content,
            UserPhoneNumber = UserPhoneNumber,
            Created = DateTimeOffset.UtcNow,
            Deleted = null
        };
    }
}
