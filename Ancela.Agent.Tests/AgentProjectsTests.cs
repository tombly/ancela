using Ancela.Agent.SemanticKernel.Plugins.ProjectsPlugin.Models;
using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// Integration tests verifying that project-related prompts trigger the correct
/// IProjectStore calls via the model's function calling. These hit a live model, so
/// they are gated under the "Integration" category and retried (via
/// <see cref="AgentTestBase.SendUntilAsync"/>) to absorb non-deterministic tool selection.
/// </summary>
[Trait("Category", "Integration")]
public class AgentProjectsTests : AgentTestBase
{
    [Fact]
    public async Task CreateProject_WhenUserStartsAProject_CallsCreateAsync()
    {
        await SendUntilAsync("start a new project called Backpacking Trip", () =>
            MockProjectStore.Verify(
                p => p.CreateAsync(It.Is<Project>(proj => proj.Name.ToLower().Contains("backpacking"))),
                Times.AtLeastOnce));
    }

    [Fact]
    public async Task ListProjects_WhenUserAsks_CallsListAsync()
    {
        SetupExistingProjects(
            CreateProjectSummary("Backpacking Trip"),
            CreateProjectSummary("Things to Sell"));

        await SendUntilAsync("what projects do I have?", () =>
            MockProjectStore.Verify(p => p.ListAsync(AgentPhoneNumber), Times.AtLeastOnce));
    }

    [Fact]
    public async Task AddEntry_WhenUserAddsItemToProject_CallsAddEntryAsync()
    {
        var projectId = Guid.NewGuid();
        SetupExistingProjects(CreateProjectSummary("Things to Sell", projectId));
        SetupProject(CreateProject("Things to Sell", projectId));
        MockProjectStore
            .Setup(p => p.AddEntryAsync(projectId, AgentPhoneNumber, It.IsAny<ProjectEntry>()))
            .ReturnsAsync(true);

        await SendUntilAsync("add a road bike to my Things to Sell project", () =>
            MockProjectStore.Verify(
                p => p.AddEntryAsync(projectId, AgentPhoneNumber,
                    It.Is<ProjectEntry>(e => e.Content.ToLower().Contains("bike"))),
                Times.AtLeastOnce));
    }

    [Fact]
    public async Task AddEntries_WhenUserAddsAList_GroupsThemUnderOneCategory()
    {
        var projectId = Guid.NewGuid();
        SetupExistingProjects(CreateProjectSummary("Backpacking Trip", projectId));
        SetupProject(CreateProject("Backpacking Trip", projectId));
        var captured = new List<ProjectEntry>();
        MockProjectStore
            .Setup(p => p.AddEntryAsync(projectId, AgentPhoneNumber, It.IsAny<ProjectEntry>()))
            .Callback<Guid, string, ProjectEntry>((_, _, e) => captured.Add(e))
            .ReturnsAsync(true);

        await SendUntilAsync(
            "add a packing list to my Backpacking Trip project with a tent, sleeping bag, and stove",
            () =>
            {
                // Each item lands as its own entry, all sharing one (case-insensitive) category
                // so they group into a single named list within the project.
                Assert.True(captured.Count >= 3, $"expected >= 3 entries, got {captured.Count}");
                Assert.All(captured, e => Assert.False(string.IsNullOrWhiteSpace(e.Category)));
                Assert.Single(captured.Select(e => e.Category!.ToLowerInvariant()).Distinct());
            },
            // Clear captured entries so a retry isn't polluted by a prior attempt's entries.
            reset: captured.Clear);
    }

    [Fact]
    public async Task ArchiveProject_WhenUserFinishes_CallsUpdateWithArchived()
    {
        var projectId = Guid.NewGuid();
        SetupExistingProjects(CreateProjectSummary("Verify Account Beneficiaries", projectId));
        SetupProject(CreateProject("Verify Account Beneficiaries", projectId));
        MockProjectStore
            .Setup(p => p.UpdateAsync(projectId, AgentPhoneNumber, It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<string?>()))
            .ReturnsAsync(true);

        await SendUntilAsync("I'm done with the Verify Account Beneficiaries project, archive it", () =>
            MockProjectStore.Verify(
                p => p.UpdateAsync(projectId, AgentPhoneNumber, It.IsAny<string?>(),
                    It.IsAny<string?>(), true, It.IsAny<string?>()),
                Times.AtLeastOnce));
    }
}
