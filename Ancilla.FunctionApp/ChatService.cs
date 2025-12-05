using System.Text;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI;

namespace Ancilla.FunctionApp;

public class ChatService(OpenAIClient _openAiClient, CosmosClient _cosmosClient, SmsService _smsService, HistoryService _historyService)
{
    public async Task<string> Chat(string message, string from, string to)
    {
        var builder = Kernel.CreateBuilder();

        // Use the injected OpenAI client from Aspire
        builder.Services.AddSingleton(_openAiClient);
        builder.AddOpenAIChatCompletion("gpt-5-mini", _openAiClient);
        builder.Services.AddLogging(services => services.AddConsole().SetMinimumLevel(LogLevel.Trace));

        var kernel = builder.Build();

        kernel.Plugins.AddFromObject(new CosmosPlugin(_cosmosClient));

        // Enable planning
        OpenAIPromptExecutionSettings openAIPromptExecutionSettings = new()
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
        };

        var history = new ChatHistory();

        var instructions = new StringBuilder();
        instructions.AppendLine("You are an AI memory assistant.");
        instructions.AppendLine("You help users save and retrieve notes via SMS.");
        instructions.AppendLine($"You just got a message from the number '{from}'.");
        instructions.AppendLine($"Your phone number is '{to}'.");
        instructions.AppendLine($"The current date and time is {DateTimeOffset.Now:f}.");
        instructions.AppendLine("Treat deleted notes as if they do not exist. Do not mention the existence of deleted notes in responses.");
        instructions.AppendLine("Be concise in your responses because they are sent via SMS.");
        history.AddSystemMessage(instructions.ToString());

        // Load chat history from Cosmos DB
        var historyEntries = await _historyService.GetHistoryAsync(from);
        foreach (var entry in historyEntries)
        {
            if (entry.messageType == MessageType.User)
            {
                history.AddUserMessage(entry.content);
            }
            else if (entry.messageType == MessageType.Assistant)
            {
                history.AddAssistantMessage(entry.content);
            }
        }

        history.AddUserMessage(message);

        var chatCompletionService = kernel.GetRequiredService<IChatCompletionService>();

        var aiResponse = await chatCompletionService.GetChatMessageContentAsync(
            history,
            executionSettings: openAIPromptExecutionSettings,
            kernel: kernel
        );

        var response = aiResponse.ToString();

        await _historyService.CreateHistoryEntryAsync(from, message, MessageType.User);
        await _historyService.CreateHistoryEntryAsync(from, response, MessageType.Assistant);

        return response;
    }

    public async Task SendReply(string message, string phoneNumber)
    {
        await _smsService.Send(phoneNumber, message);
    }
}