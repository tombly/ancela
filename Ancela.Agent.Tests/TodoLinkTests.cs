using Ancela.Agent.SemanticKernel.Plugins.MemoryPlugin;
using Ancela.Agent.SemanticKernel.Plugins.MemoryPlugin.Models;
using Ancela.Agent.Services;
using FluentAssertions;
using Microsoft.SemanticKernel;
using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// get_todos shows links as (link) unless asked for them, so listing to-dos doesn't send a
/// several-hundred-character link every time. The to-do itself keeps the whole link.
/// </summary>
public class TodoLinkTests
{
    private const string AgentPhoneNumber = "+15550000000";
    private const string LongLink = "https://events.example.com/join/Xq7Lm2Rt9Vb4Kp8Wn3Hs6Jd1Fg5Zc0Ya~Te4Uo7Ii2Pr9Ee6Ww3Qq8-Ss1Dd5Ff0Gg-Hh4Jj7Kk";

    private readonly Mock<IMemoryClient> _memory = new();
    private readonly MemoryPlugin _plugin;
    private readonly Kernel _kernel = new();

    public TodoLinkTests()
    {
        Environment.SetEnvironmentVariable("TWILIO_PHONE_NUMBER", "+10000000000");
        Environment.SetEnvironmentVariable("TWILIO_ACCOUNT_SID", "ACXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX");
        Environment.SetEnvironmentVariable("TWILIO_AUTH_TOKEN", "test-token");
        Environment.SetEnvironmentVariable("OWNER_PHONE_NUMBER", "+15551234567");
        _plugin = new MemoryPlugin(_memory.Object, new SmsService(), new OwnerService());
        _kernel.Data["agentPhoneNumber"] = AgentPhoneNumber;
        _kernel.Data["userPhoneNumber"] = "+15551234567";
    }

    private void SetupTodos(params string[] contents) =>
        _memory.Setup(m => m.GetToDosAsync(AgentPhoneNumber)).ReturnsAsync(
            [.. contents.Select(c => new ToDoModel { Id = Guid.NewGuid(), Content = c, Created = DateTimeOffset.UtcNow, Deleted = null })]);

    [Fact]
    public async Task Listing_ShowsLinksAsLink()
    {
        SetupTodos($"Watch the school talk: {LongLink}", "Buy milk");

        var todos = await _plugin.GetToDosAsync(_kernel);

        todos.Select(t => t.Content).Should().Equal("Watch the school talk: (link)", "Buy milk");
    }

    [Fact]
    public async Task Listing_KeepsTheTextAroundALink()
    {
        SetupTodos($"Read {LongLink} and HTTP://EXAMPLE.ORG/x before Friday");

        var todos = await _plugin.GetToDosAsync(_kernel);

        todos.Single().Content.Should().Be("Read (link) and (link) before Friday");
    }

    [Fact]
    public async Task Listing_KeepsIdsAndDates_SoDeleteStillWorks()
    {
        var stored = new ToDoModel { Id = Guid.NewGuid(), Content = $"Watch {LongLink}", Created = DateTimeOffset.UtcNow.AddDays(-3), Deleted = null };
        _memory.Setup(m => m.GetToDosAsync(AgentPhoneNumber)).ReturnsAsync([stored]);

        var listed = (await _plugin.GetToDosAsync(_kernel)).Single();

        listed.Id.Should().Be(stored.Id);
        listed.Created.Should().Be(stored.Created);
        stored.Content.Should().Be($"Watch {LongLink}", "the stored to-do is not changed");
    }

    [Fact]
    public async Task IncludeLinks_ReturnsThemInFull()
    {
        SetupTodos($"Watch the school talk: {LongLink}");

        var todos = await _plugin.GetToDosAsync(_kernel, includeLinks: true);

        todos.Single().Content.Should().Be($"Watch the school talk: {LongLink}");
    }
}
