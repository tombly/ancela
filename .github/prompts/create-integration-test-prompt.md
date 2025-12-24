# Adding Integration Tests for AI Function Calling

Use this prompt when you need to add integration tests that verify AI function calling behavior in the Ancilla project.

## Context

Ancilla uses Semantic Kernel with OpenAI models to invoke functions based on natural language prompts. Integration tests verify that specific prompts result in the correct function calls by the AI model.

## Test Strategy

- **Real AI calls**: Use actual OpenAI API (not mocked) to test function call selection
- **Mocked data services**: Mock data layer to capture and verify function invocations
- **Behavior verification**: Assert that correct functions are called, not data persistence

## What to Test

When adding new AI capabilities:
1. **Save operations**: "Add X" → `SaveXAsync` called
2. **Retrieve operations**: "Show me X" → `GetXAsync` called  
3. **Delete operations**: "Remove X" → `DeleteXAsync` called
4. **Edge cases**: Ambiguous prompts, missing data, error conditions

## Implementation Checklist

### 1. Ensure interfaces exist
- [ ] Service has interface (e.g., `IMyService`)
- [ ] Interface methods are public and testable
- [ ] `Program.cs` registers interface → implementation

### 2. Create test class
- [ ] Inherit from `ChatServiceTestBase`
- [ ] Use `[Fact]` for simple tests or `[Theory]` for parameterized tests
- [ ] Name tests descriptively: `MethodName_WhenCondition_ExpectedBehavior`

### 3. Write test
```csharp
[Fact]
public async Task MyFunction_WhenPromptGiven_CallsExpectedService()
{
    // Arrange: Setup mocks if needed (e.g., SetupExistingTodos)
    
    // Act: Send prompt to chat service
    await SendMessageAsync("your test prompt here");
    
    // Assert: Verify function was called
    MockMyService.Verify(
        s => s.MyMethodAsync(
            It.IsAny<string>(),
            It.IsAny<OtherParams>()),
        Times.Once);
}
```

### 4. Handle LLM variability
- Use **explicit, unambiguous prompts** ("delete the todo about milk" not "forget milk")
- Verify **function call type** with `It.IsAny<>()` for parameters
- If flaky, adjust prompt wording for clarity
- Set temperature low for deterministic responses (configured in `ChatService`)

### 5. Run tests
```bash
dotnet test Ancilla.FunctionApp.Tests
```

Tests run sequentially (`DisableTestParallelization = true`) to avoid race conditions with OpenAI API.

## Example Test Structure

```csharp
public class ChatServiceMyFeatureTests : ChatServiceTestBase
{
    [Fact]
    public async Task SaveData_WhenUserAsksToRemember_SavesData()
    {
        // Act
        await SendMessageAsync("remember my favorite color is blue");
        
        // Assert
        MockMyService.Verify(
            s => s.SaveAsync(
                AgentPhoneNumber,
                It.Is<MyEntry>(e => e.Content.Contains("blue"))),
            Times.Once);
    }

    [Fact]
    public async Task GetData_WhenUserAsksQuestion_RetrievesData()
    {
        // Arrange
        SetupExistingData(CreateMyData("blue"));
        
        // Act
        await SendMessageAsync("what is my favorite color");
        
        // Assert
        MockMyService.Verify(
            s => s.GetAsync(AgentPhoneNumber),
            Times.Once);
    }

    [Fact]
    public async Task DeleteData_WhenUserAsksToForget_DeletesData()
    {
        // Arrange
        var existingData = CreateMyData("blue");
        SetupExistingData(existingData);
        
        // Act
        await SendMessageAsync("delete my favorite color");
        
        // Assert
        MockMyService.Verify(
            s => s.DeleteAsync(
                AgentPhoneNumber,
                existingData.Id),
            Times.Once);
    }
}
```

## Setup Requirements

### API Key Configuration
Tests require OpenAI API key in user secrets:
```bash
cd Ancilla.AppHost
dotnet user-secrets set "Parameters:openai-api-key" "your-key-here"
```

### Helper Methods Available
From `ChatServiceTestBase`:
- `SendMessageAsync(string message)` - Send prompt and get response
- `SetupExistingTodos(params TodoEntry[])` - Mock existing todos
- `SetupExistingKnowledge(params KnowledgeEntry[])` - Mock existing knowledge
- `CreateTodo(string content, Guid? id)` - Create test todo
- `CreateKnowledge(string content, Guid? id)` - Create test knowledge

### Test Data
- `AgentPhoneNumber` - Ancilla's phone number ("+15551234567")
- `UserPhoneNumber` - Test user's phone number ("+15559876543")
- `TestSession` - Pre-configured session entry

## Common Patterns

### Testing with existing data
```csharp
var existingTodo = CreateTodo("buy milk");
SetupExistingTodos(existingTodo);
await SendMessageAsync("delete the milk todo");
MockTodoService.Verify(s => s.DeleteTodoAsync(AgentPhoneNumber, existingTodo.Id), Times.Once);
```

### Testing multiple scenarios
```csharp
[Theory]
[InlineData("remind me to X")]
[InlineData("add a todo: X")]
[InlineData("don't let me forget to X")]
public async Task SaveTodo_WithVariousPrompts_CallsSaveTodo(string promptTemplate)
{
    await SendMessageAsync(promptTemplate.Replace("X", "buy groceries"));
    MockTodoService.Verify(s => s.SaveTodoAsync(It.IsAny<string>(), It.IsAny<TodoEntry>()), Times.Once);
}
```

## Troubleshooting

| Issue | Solution |
|-------|----------|
| Test fails intermittently | Make prompt more explicit and specific |
| Mock not being called | Verify AI has access to the function (check `CosmosPlugin`) |
| API key not found | Ensure user secrets are configured correctly |
| Tests interfere with each other | Already handled: parallelization is disabled |
| Wrong function called | Improve system prompt or function descriptions |

## Cost Considerations

Each test makes a real OpenAI API call (~$0.001-0.01 per test). Consider:
- Run tests before committing to catch issues early
- Don't run unnecessarily in CI (or use cheaper models)
- Group related tests to minimize test runs
