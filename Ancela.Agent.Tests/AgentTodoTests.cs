using Ancela.Agent.SemanticKernel.Plugins.ScheduledTaskPlugin.Models;
using FluentAssertions;
using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// Integration tests verifying that todo-related prompts trigger the correct
/// memory function calls via the AI's function calling capability. These hit a live
/// model, so they are gated under the "Integration" category and retried (via
/// <see cref="AgentTestBase.SendUntilAsync"/>) to absorb non-deterministic tool selection.
/// </summary>
[Trait("Category", "Integration")]
public class AgentTodoTests : AgentTestBase
{
    [Fact]
    public async Task SaveTodo_WhenUserAsksToRememberTask_CallsSaveTodoAsync()
    {
        // Use clear to-do phrasing: "remind me to…" now reads as a reminder (a separate
        // feature), so it no longer reliably routes to save_todo.
        await SendUntilAsync("add a to-do to buy milk", () =>
            MockMemoryClient.Verify(
                m => m.SaveToDoAsync(
                    AgentPhoneNumber,
                    UserPhoneNumber,
                    It.Is<string>(content => content.ToLower().Contains("milk")),
                    null),
                Times.AtLeastOnce));
    }

    [Fact]
    public async Task GetTodos_WhenUserAsksForList_CallsGetTodosAsync()
    {
        SetupExistingTodos(
            CreateTodo("Buy groceries"),
            CreateTodo("Call mom"));

        await SendUntilAsync("what are my todos?", () =>
            MockMemoryClient.Verify(
                m => m.GetToDosAsync(AgentPhoneNumber),
                Times.AtLeastOnce));
    }

    [Fact]
    public async Task GetTodos_WhenUserAsksToShowTasks_CallsGetTodosAsync()
    {
        SetupExistingTodos(CreateTodo("Walk the dog"));

        await SendUntilAsync("show me my tasks", () =>
            MockMemoryClient.Verify(
                m => m.GetToDosAsync(AgentPhoneNumber),
                Times.AtLeastOnce));
    }

    [Fact]
    public async Task DeleteTodo_WhenUserAsksToRemoveTodo_CallsDeleteTodoAsync()
    {
        var todoId = Guid.NewGuid();
        SetupExistingTodos(CreateTodo("Buy milk", todoId));

        await SendUntilAsync("delete the milk todo", () =>
            MockMemoryClient.Verify(
                m => m.DeleteToDoAsync(todoId, AgentPhoneNumber),
                Times.AtLeastOnce));
    }

    private const string LongLink = "https://events.example.com/join/Xq7Lm2Rt9Vb4Kp8Wn3Hs6Jd1Fg5Zc0Ya~Te4Uo7Ii2Pr9Ee6Ww3Qq8-Ss1Dd5Ff0Gg-Hh4Jj7Kk";

    [Fact]
    public async Task ListTodos_LeavesTheLinkOut()
    {
        SetupExistingTodos(CreateTodo($"Watch the school talk: {LongLink}"), CreateTodo("Buy milk"));

        await SendUntilAsync("what's on my to-do list?", reply =>
        {
            reply.Should().Contain("school talk");
            reply.Should().NotContain("events.example.com");
        });
    }

    [Fact]
    public async Task AskForTheLink_SendsItInFull()
    {
        SetupExistingTodos(CreateTodo($"Watch the school talk: {LongLink}"), CreateTodo("Buy milk"));

        await SendUntilAsync("send me the link for the school talk on my to-do list", reply =>
            reply.Should().Contain(LongLink));
    }

    [Fact]
    public async Task DeleteTodo_WhenUserMarksTaskComplete_CallsDeleteTodoAsync()
    {
        var todoId = Guid.NewGuid();
        SetupExistingTodos(CreateTodo("Finish report", todoId));

        await SendUntilAsync("I finished the report, you can remove it", () =>
            MockMemoryClient.Verify(
                m => m.DeleteToDoAsync(todoId, AgentPhoneNumber),
                Times.AtLeastOnce));
    }

    [Fact]
    public async Task SaveTodo_WithADeadline_StoresItAsTheDueDate()
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById(TestUserTimeZone)).DateTime);
        var nov15 = new DateOnly(today.Year, 11, 15);
        if (nov15 < today)
            nov15 = nov15.AddYears(1);

        await SendUntilAsync("I have to renew my vehicle tags by Nov 15th", () =>
            MockMemoryClient.Verify(
                m => m.SaveToDoAsync(AgentPhoneNumber, UserPhoneNumber,
                    It.Is<string>(content => content.Contains("tag", StringComparison.OrdinalIgnoreCase)), nov15),
                Times.AtLeastOnce));
    }

    [Fact]
    public async Task MorningBriefing_WarnsAboutTodosDueSoon_AndNotTheRest()
    {
        // The use the due date exists for: a scheduled briefing reads get_todos and warns about
        // what is close, and Ancela never reminds on its own.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById(TestUserTimeZone)).DateTime);
        SetupExistingTodos(
            CreateTodo("Renew vehicle tags", due: today.AddDays(5)),
            CreateTodo("File the property tax appeal", due: today.AddDays(45)),
            CreateTodo("Buy milk"));
        var briefing = new ScheduledTask
        {
            Id = Guid.NewGuid(),
            UserPhoneNumber = UserPhoneNumber,
            AgentPhoneNumber = AgentPhoneNumber,
            Description = "Morning briefing: warn me about any to-dos due in the next two weeks, with how many days are left.",
            TimeOfDay = "07:30",
            DaysOfWeek = [0, 1, 2, 3, 4, 5, 6],
        };

        string? reply = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            reply = await Agent.PerformScheduledTask(briefing, TestUser);
            if (reply.Contains("tags", StringComparison.OrdinalIgnoreCase) && reply.Contains('5'))
                break;
        }

        reply.Should().ContainEquivalentOf("tags").And.Contain("5", because: $"the tags are due in 5 days; the reply was: {reply}");
        reply.Should().NotContainEquivalentOf("tax appeal", because: "it isn't due for 45 days");
    }
}
