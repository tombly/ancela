using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI;

namespace Ancilla.FunctionApp.Services;

public class ChatService(OpenAIClient _openAiClient, ITodoService _todoService, IKnowledgeService _knowledgeService, IHistoryService _historyService, IGraphService _graphService)
{
    public async Task<string> Chat(string message, string userPhoneNumber, string agentPhoneNumber, SessionEntry session)
    {
        var builder = Kernel.CreateBuilder();

        // Use the injected OpenAI client from Aspire.
        builder.Services.AddSingleton(_openAiClient);
        builder.AddOpenAIChatCompletion("gpt-5-mini", _openAiClient);
        builder.Services.AddLogging(services => services.AddConsole().SetMinimumLevel(LogLevel.Trace));

        var kernel = builder.Build();

        // Register plugins.
        kernel.Plugins.AddFromObject(new CosmosPlugin(_todoService, _knowledgeService));
        kernel.Plugins.AddFromObject(new GraphPlugin(_graphService));

        // Enable planning.
        var openAIPromptExecutionSettings = new OpenAIPromptExecutionSettings()
        { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() };

        var history = new ChatHistory();

        var timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(session.TimeZone);
        var localTime = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZoneInfo);

        var instructions = $"""
            - You are an AI agent named Ancilla.
            - You help users remember (1) things to do and (2) general knowledge.
            - You are a singlular AI instance serving multiple users. 
            - Your phone number is '{agentPhoneNumber}'.
            - You are currently chatting with a user whose phone number is '{userPhoneNumber}'.
            - You have a memory of todos and knowledge (a database with basic CRUD operations).
            - Use your judgement to decide whether the user is talking about todos or knowledge.
              In general, todos will be action-oriented items the user wants to remember to do later.
            - You can create, read, update, and delete todos and knowledge entries using your memory
              functions as needed.
            - For the todos, they do not have due dates and you are not able to remind the user proactively.
            - You have access to the individual conversation history with each user.
            - You have read-only access to the user's calendar events.
            - The user's current local date and time is {localTime:f} ({session.TimeZone}).
            - Be concise in your responses because they are sent via SMS.
            - When a user asks you to 'list my todos', respond with a numbered
              list of todo titles, oldest first. Always exclude deleted todos.
            - You have a separate chat history for each user, but your memory is
              shared across all users.
            """;
        history.AddSystemMessage(instructions);

        // Load chat history from database.
        var historyEntries = await _historyService.GetHistoryAsync(agentPhoneNumber, userPhoneNumber);
        foreach (var entry in historyEntries)
        {
            if (entry.MessageType == MessageType.User)
                history.AddUserMessage(entry.Content);
            else if (entry.MessageType == MessageType.Assistant)
                history.AddAssistantMessage(entry.Content);
        }

        history.AddUserMessage(message);

        // Populate kernel arguments with contextual data
        kernel.Data["agentPhoneNumber"] = agentPhoneNumber;
        kernel.Data["userPhoneNumber"] = userPhoneNumber;

        var chatCompletionService = kernel.GetRequiredService<IChatCompletionService>();

        var aiResponse = await chatCompletionService.GetChatMessageContentAsync(
            history,
            executionSettings: openAIPromptExecutionSettings,
            kernel: kernel
        );

        var response = aiResponse.ToString();

        await _historyService.CreateHistoryEntryAsync(agentPhoneNumber, userPhoneNumber, message, MessageType.User);
        await _historyService.CreateHistoryEntryAsync(agentPhoneNumber, userPhoneNumber, response, MessageType.Assistant);

        return response;
    }
}