using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// Integration tests verifying that knowledge-related prompts trigger the correct
/// IKnowledgeService function calls via the AI's function calling capability.
/// </summary>
public class AgentKnowledgeTests : AgentTestBase
{
    [Fact]
    public async Task SaveKnowledge_WhenUserAsksToRememberFact_CallsSaveKnowledgeAsync()
    {
        // Act
        var response = await SendMessageAsync("remember that my doctor is Dr. Smith");

        // Assert
        MockMemoryClient.Verify(
            m => m.SaveKnowledgeAsync(
                AgentPhoneNumber,
                UserPhoneNumber,
                It.Is<string>(content => content.ToLower().Contains("smith") || content.ToLower().Contains("doctor"))),
            Times.Once);
    }

    [Fact]
    public async Task SaveKnowledge_WhenUserSharesPersonalInfo_CallsSaveKnowledgeAsync()
    {
        // Act
        var response = await SendMessageAsync("my favorite color is blue");

        // Assert
        MockMemoryClient.Verify(
            m => m.SaveKnowledgeAsync(
                AgentPhoneNumber,
                UserPhoneNumber,
                It.Is<string>(content => content.ToLower().Contains("blue") || content.ToLower().Contains("color"))),
            Times.Once);
    }

    [Fact]
    public async Task GetKnowledge_WhenUserAsksAboutStoredFact_CallsGetKnowledgeAsync()
    {
        // Arrange
        SetupExistingKnowledge(
            CreateKnowledge("User's doctor is Dr. Smith"),
            CreateKnowledge("User's favorite color is blue"));

        // Act
        var response = await SendMessageAsync("who is my doctor?");

        // Assert
        MockMemoryClient.Verify(
            m => m.GetKnowledgeAsync(AgentPhoneNumber),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetKnowledge_WhenUserAsksWhatYouKnow_CallsGetKnowledgeAsync()
    {
        // Arrange
        SetupExistingKnowledge(CreateKnowledge("User works at Acme Corp"));

        // Act
        var response = await SendMessageAsync("what do you know about me?");

        // Assert
        MockMemoryClient.Verify(
            m => m.GetKnowledgeAsync(AgentPhoneNumber),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task DeleteKnowledge_WhenUserAsksToForget_CallsDeleteKnowledgeAsync()
    {
        // Arrange
        var knowledgeId = Guid.NewGuid();
        SetupExistingKnowledge(CreateKnowledge("User's doctor is Dr. Smith", knowledgeId));

        // Act - Use explicit phrasing to ensure the AI calls delete
        var response = await SendMessageAsync("delete the knowledge about my doctor");

        // Assert
        MockMemoryClient.Verify(
            m => m.DeleteKnowledgeAsync(knowledgeId, AgentPhoneNumber),
            Times.Once);
    }

    [Fact]
    public async Task DeleteKnowledge_WhenUserSaysInfoIsOutdated_CallsDeleteKnowledgeAsync()
    {
        // Arrange
        var knowledgeId = Guid.NewGuid();
        SetupExistingKnowledge(CreateKnowledge("User's favorite color is blue", knowledgeId));

        // Act
        var response = await SendMessageAsync("that's not my favorite color anymore, please remove it");

        // Assert
        MockMemoryClient.Verify(
            m => m.DeleteKnowledgeAsync(knowledgeId, AgentPhoneNumber),
            Times.Once);
    }
}
