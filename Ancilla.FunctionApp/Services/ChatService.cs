using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI;

namespace Ancilla.FunctionApp.Services;

public class ChatService(OpenAIClient _openAiClient, NoteService _noteService, SmsService _smsService, HistoryService _historyService)
{
    public async Task<string> Chat(string message, string from, string to)
    {
        var builder = Kernel.CreateBuilder();

        // Use the injected OpenAI client from Aspire.
        builder.Services.AddSingleton(_openAiClient);
        builder.AddOpenAIChatCompletion("gpt-5-mini", _openAiClient);
        builder.Services.AddLogging(services => services.AddConsole().SetMinimumLevel(LogLevel.Trace));

        var kernel = builder.Build();

        kernel.Plugins.AddFromObject(new CosmosPlugin(_noteService));

        // Enable planning.
        OpenAIPromptExecutionSettings openAIPromptExecutionSettings = new()
            { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() };

        var history = new ChatHistory();

        var instructions = new StringBuilder();
        instructions.AppendLine("You are an AI memory assistant.");
        instructions.AppendLine("You help users remember things by saving and retrieving notes.");
        instructions.AppendLine($"You just got a message from the user with phone number '{from}'.");
        instructions.AppendLine($"Your phone number is '{to}'.");
        instructions.AppendLine($"The current date and time is {DateTimeOffset.Now:f}.");
        instructions.AppendLine("Be concise in your responses because they are sent via SMS.");
        instructions.AppendLine("When a user asks you to 'list my notes', respond with a numbered list of note titles. Always exclude deleted notes.");
        history.AddSystemMessage(instructions.ToString());

        // Load chat history from database.
        var historyEntries = await _historyService.GetHistoryAsync(to, from);
        foreach (var entry in historyEntries)
        {
            if (entry.MessageType == MessageType.User)
                history.AddUserMessage(entry.Content);
            else if (entry.MessageType == MessageType.Assistant)
                history.AddAssistantMessage(entry.Content);
        }

        history.AddUserMessage(message);

        var chatCompletionService = kernel.GetRequiredService<IChatCompletionService>();

        var aiResponse = await chatCompletionService.GetChatMessageContentAsync(
            history,
            executionSettings: openAIPromptExecutionSettings,
            kernel: kernel
        );

        var response = aiResponse.ToString();

        await _historyService.CreateHistoryEntryAsync(to, from, message, MessageType.User);
        await _historyService.CreateHistoryEntryAsync(to, from, response, MessageType.Assistant);

        return response;
    }

    public async Task SendReply(string message, string phoneNumber)
    {
        await _smsService.Send(phoneNumber, message);
    }
}