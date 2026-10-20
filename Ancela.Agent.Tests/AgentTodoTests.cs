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
                    It.Is<string>(content => content.ToLower().Contains("milk"))),
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
}
