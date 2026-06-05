using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// Integration tests verifying that knowledge-related prompts trigger the correct
/// memory function calls via the AI's function calling capability. These hit a live
/// model, so they are gated under the "Integration" category and retried (via
/// <see cref="AgentTestBase.SendUntilAsync"/>) to absorb non-deterministic tool selection.
/// </summary>
[Trait("Category", "Integration")]
public class AgentKnowledgeTests : AgentTestBase
{
    [Fact]
    public async Task SaveKnowledge_WhenUserAsksToRememberFact_CallsSaveKnowledgeAsync()
    {
        await SendUntilAsync("remember that my doctor is Dr. Smith", () =>
            MockMemoryClient.Verify(
                m => m.SaveKnowledgeAsync(
                    AgentPhoneNumber,
                    UserPhoneNumber,
                    It.Is<string>(content => content.ToLower().Contains("smith") || content.ToLower().Contains("doctor"))),
                Times.AtLeastOnce));
    }

    [Fact]
    public async Task GetKnowledge_WhenUserAsksAboutStoredFact_CallsGetKnowledgeAsync()
    {
        SetupExistingKnowledge(
            CreateKnowledge("User's doctor is Dr. Smith"),
            CreateKnowledge("User's favorite color is blue"));

        await SendUntilAsync("who is my doctor?", () =>
            MockMemoryClient.Verify(
                m => m.GetKnowledgeAsync(AgentPhoneNumber),
                Times.AtLeastOnce));
    }

    [Fact]
    public async Task GetKnowledge_WhenUserAsksWhatYouKnow_CallsGetKnowledgeAsync()
    {
        SetupExistingKnowledge(CreateKnowledge("User works at Acme Corp"));

        await SendUntilAsync("what do you know about me?", () =>
            MockMemoryClient.Verify(
                m => m.GetKnowledgeAsync(AgentPhoneNumber),
                Times.AtLeastOnce));
    }

    [Fact]
    public async Task DeleteKnowledge_WhenUserAsksToForget_CallsDeleteKnowledgeAsync()
    {
        var knowledgeId = Guid.NewGuid();
        SetupExistingKnowledge(CreateKnowledge("User's doctor is Dr. Smith", knowledgeId));

        await SendUntilAsync("delete the knowledge about my doctor", () =>
            MockMemoryClient.Verify(
                m => m.DeleteKnowledgeAsync(knowledgeId, AgentPhoneNumber),
                Times.AtLeastOnce));
    }

    [Fact]
    public async Task DeleteKnowledge_WhenUserSaysInfoIsOutdated_CallsDeleteKnowledgeAsync()
    {
        var knowledgeId = Guid.NewGuid();
        SetupExistingKnowledge(CreateKnowledge("User's favorite color is blue", knowledgeId));

        await SendUntilAsync("that's not my favorite color anymore, please remove it", () =>
            MockMemoryClient.Verify(
                m => m.DeleteKnowledgeAsync(knowledgeId, AgentPhoneNumber),
                Times.AtLeastOnce));
    }
}
