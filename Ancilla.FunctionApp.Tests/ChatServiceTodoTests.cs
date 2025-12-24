using Moq;

namespace Ancilla.FunctionApp.Tests;

/// <summary>
/// Integration tests verifying that todo-related prompts trigger the correct
/// ITodoService function calls via the AI's function calling capability.
/// </summary>
public class ChatServiceTodoTests : ChatServiceTestBase
{
    [Fact]
    public async Task SaveTodo_WhenUserAsksToRememberTask_CallsSaveTodoAsync()
    {
        // Act
        var response = await SendMessageAsync("remind me to buy milk");

        // Assert
        MockTodoService.Verify(
            t => t.SaveTodoAsync(
                AgentPhoneNumber,
                UserPhoneNumber,
                It.Is<string>(content => content.ToLower().Contains("milk"))),
            Times.Once);
    }

    [Fact]
    public async Task SaveTodo_WhenUserUsesAddTodoPhrase_CallsSaveTodoAsync()
    {
        // Act
        var response = await SendMessageAsync("add a todo to call the dentist");

        // Assert
        MockTodoService.Verify(
            t => t.SaveTodoAsync(
                AgentPhoneNumber,
                UserPhoneNumber,
                It.Is<string>(content => content.ToLower().Contains("dentist"))),
            Times.Once);
    }

    [Fact]
    public async Task GetTodos_WhenUserAsksForList_CallsGetTodosAsync()
    {
        // Arrange
        SetupExistingTodos(
            CreateTodo("Buy groceries"),
            CreateTodo("Call mom"));

        // Act
        var response = await SendMessageAsync("what are my todos?");

        // Assert
        MockTodoService.Verify(
            t => t.GetTodosAsync(AgentPhoneNumber),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetTodos_WhenUserAsksToShowTasks_CallsGetTodosAsync()
    {
        // Arrange
        SetupExistingTodos(CreateTodo("Walk the dog"));

        // Act
        var response = await SendMessageAsync("show me my tasks");

        // Assert
        MockTodoService.Verify(
            t => t.GetTodosAsync(AgentPhoneNumber),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task DeleteTodo_WhenUserAsksToRemoveTodo_CallsDeleteTodoAsync()
    {
        // Arrange
        var todoId = Guid.NewGuid();
        SetupExistingTodos(CreateTodo("Buy milk", todoId));

        // Act
        var response = await SendMessageAsync("delete the milk todo");

        // Assert
        MockTodoService.Verify(
            t => t.DeleteTodoAsync(todoId, AgentPhoneNumber),
            Times.Once);
    }

    [Fact]
    public async Task DeleteTodo_WhenUserMarksTaskComplete_CallsDeleteTodoAsync()
    {
        // Arrange
        var todoId = Guid.NewGuid();
        SetupExistingTodos(CreateTodo("Finish report", todoId));

        // Act
        var response = await SendMessageAsync("I finished the report, you can remove it");

        // Assert
        MockTodoService.Verify(
            t => t.DeleteTodoAsync(todoId, AgentPhoneNumber),
            Times.Once);
    }
}
