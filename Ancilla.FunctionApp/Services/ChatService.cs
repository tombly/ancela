using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI;

namespace Ancilla.FunctionApp.Services;

public class ChatService(OpenAIClient _openAiClient, NoteService _noteService, HistoryService _historyService)
{
    public async Task<string> Chat(string message, string userPhoneNumber, string agentPhoneNumber, SessionEntry session)
    {
        var builder = Kernel.CreateBuilder();

        // Use the injected OpenAI client from Aspire.
        builder.Services.AddSingleton(_openAiClient);
        builder.AddOpenAIChatCompletion("gpt-5-mini", _openAiClient);
        builder.Services.AddLogging(services => services.AddConsole().SetMinimumLevel(LogLevel.Trace));

        var kernel = builder.Build();

        kernel.Plugins.AddFromObject(new CosmosPlugin(_noteService));

        // Enable planning.
        var openAIPromptExecutionSettings = new OpenAIPromptExecutionSettings()
        { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() };

        var history = new ChatHistory();

        var timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(session.TimeZone);
        var localTime = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZoneInfo);

        var instructions = $"""
            - You are an AI agent named Ancilla.
            - You help users remember things by saving and retrieving notes.
            - Your phone number is '{agentPhoneNumber}'.
            - You are currently chatting with a user whose phone number is '{userPhoneNumber}'.
            - You have access to a database of notes associated with this user.
            - You have access to the current conversation history.
            - The user's current local date and time is {localTime:f} ({session.TimeZone}).
            - Be concise in your responses because they are sent via SMS.
            - When a user asks you to 'list my notes', respond with a numbered
              list of note titles, oldest first. Always exclude deleted notes.
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