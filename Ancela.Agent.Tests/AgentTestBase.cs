using Ancela.Agent.SemanticKernel;
using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using Ancela.Agent.SemanticKernel.Plugins.MemoryPlugin;
using Ancela.Agent.SemanticKernel.Plugins.MemoryPlugin.Models;
using Ancela.Agent.SemanticKernel.Plugins.ProjectsPlugin;
using Ancela.Agent.SemanticKernel.Plugins.ProjectsPlugin.Models;
using Ancela.Agent.SemanticKernel.Plugins.SmsPlugin;
using Ancela.Agent.SemanticKernel.Plugins.YnabPlugin;
using Ancela.Agent.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
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
    protected readonly Mock<IProjectStore> MockProjectStore;

    // System under test
    protected readonly Agent Agent;

    // Test user profile
    protected readonly UserProfile TestUser;

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
        MockProjectStore = new Mock<IProjectStore>();

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

        // Default: no projects
        MockProjectStore
            .Setup(p => p.ListAsync(It.IsAny<string>()))
            .ReturnsAsync(Array.Empty<ProjectSummary>());

        // SmsPlugin requires Twilio configuration. Provide dummy values for tests.
        Environment.SetEnvironmentVariable("TWILIO_PHONE_NUMBER", "+10000000000");
        Environment.SetEnvironmentVariable("TWILIO_ACCOUNT_SID", "ACXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX");
        Environment.SetEnvironmentVariable("TWILIO_AUTH_TOKEN", "test-token");
        var smsService = new SmsService();

        // Treat the test user as the owner so no owner-only function filtering applies here.
        Environment.SetEnvironmentVariable("OWNER_PHONE_NUMBER", UserPhoneNumber);
        var ownerService = new OwnerService();
        var smsPlugin = new SmsPlugin(smsService);

        // Create plugins with mocked clients
        var memoryPlugin = new MemoryPlugin(MockMemoryClient.Object, smsService, ownerService);
        var graphPlugin = new GraphPlugin(MockGraphClient.Object);
        var projectsPlugin = new ProjectsPlugin(MockProjectStore.Object);

        // YnabPlugin requires YnabClient. For tests, set a dummy token to avoid exceptions
        // The actual YNAB tests would need to mock the YnabClient or use integration testing
        Environment.SetEnvironmentVariable("YNAB_ACCESS_TOKEN", "test-token-for-testing");
        var ynabClient = new YnabClient();
        var ynabPlugin = new YnabPlugin(ynabClient);

        // Create kernel with real OpenAI and plugins with mocked clients
        var chatCompletionService = new OpenAIChatCompletionService("gpt-5-mini", OpenAIClient);

        // Build a factory that hands out a fresh kernel (with the test plugins) per call
        var kernelFactory = new Mock<IKernelFactory>();
        kernelFactory
            .Setup(f => f.Create(It.IsAny<KernelProfile>()))
            .Returns(() =>
            {
                var pluginCollection = new KernelPluginCollection();
                pluginCollection.AddFromObject(graphPlugin);
                pluginCollection.AddFromObject(memoryPlugin);
                pluginCollection.AddFromObject(projectsPlugin);
                pluginCollection.AddFromObject(ynabPlugin);
                pluginCollection.AddFromObject(smsPlugin);
                return new Kernel(plugins: pluginCollection);
            });

        // Create agent with real OpenAI and mocked data services
        Agent = new Agent(
            kernelFactory.Object,
            chatCompletionService,
            MockHistoryService.Object,
            new CorrelationContext(),
            ownerService);

        // Create test user profile
        TestUser = new UserProfile
        {
            Id = Guid.NewGuid(),
            AgentPhoneNumber = AgentPhoneNumber,
            UserPhoneNumber = UserPhoneNumber,
            Name = "Test User",
            TimeZone = "America/Los_Angeles",
            Location = "San Francisco, CA",
            CreatedAt = DateTimeOffset.UtcNow,
            RegisteredAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Sends a message to the agent and returns the response.
    /// </summary>
    protected Task<string> SendMessageAsync(string message)
    {
        return Agent.Chat(message, UserPhoneNumber, AgentPhoneNumber, TestUser, []);
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

    /// <summary>Configures list_projects to return the given project summaries.</summary>
    protected void SetupExistingProjects(params ProjectSummary[] projects)
    {
        MockProjectStore
            .Setup(p => p.ListAsync(AgentPhoneNumber))
            .ReturnsAsync(projects);
    }

    /// <summary>Configures get_project to return the given project (by its ID).</summary>
    protected void SetupProject(Project project)
    {
        MockProjectStore
            .Setup(p => p.GetAsync(project.Id, AgentPhoneNumber))
            .ReturnsAsync(project);
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

    /// <summary>Creates a Project for testing.</summary>
    protected static Project CreateProject(string name, Guid? id = null, string? purpose = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new Project
        {
            Id = id ?? Guid.NewGuid(),
            AgentPhoneNumber = AgentPhoneNumber,
            UserPhoneNumber = UserPhoneNumber,
            Name = name,
            Purpose = purpose,
            Notes = "",
            IsArchived = false,
            Entries = [],
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    /// <summary>Creates a ProjectSummary for testing.</summary>
    protected static ProjectSummary CreateProjectSummary(string name, Guid? id = null, string? purpose = null)
    {
        return new ProjectSummary { Id = id ?? Guid.NewGuid(), Name = name, Purpose = purpose };
    }
}
