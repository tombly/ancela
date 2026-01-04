using Ancela.Agent.SemanticKernel.Plugins.ChatPlugin;
using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using Ancela.Agent.SemanticKernel.Plugins.MemoryPlugin;
using Ancela.Agent.SemanticKernel.Plugins.YnabPlugin;
using Ancela.Agent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI;

namespace Ancela.Agent;

public class Agent(OpenAIClient _openAiClient, IHistoryService _historyService, MemoryPlugin _memoryPlugin, GraphPlugin _graphPlugin, YnabPlugin _ynabPlugin, LoopbackPlugin _loopbackPlugin)
{
    public async Task<string> Chat(string message, string userPhoneNumber, string agentPhoneNumber, SessionEntry session, string[] mediaUrls)
    {
        // TODO: Handle media URLs: Save to blob storage with metadata stored by the memory plugin.
        //       Use image analysis to describe images and extract text (store both with metadata).
        //       Allow only images for now.
        //       Need to handle the scenario where there is no message and only media.  

        var builder = Kernel.CreateBuilder();

        // Use the injected OpenAI client from Aspire.
        builder.Services.AddSingleton(_openAiClient);
        builder.AddOpenAIChatCompletion("gpt-5-mini", _openAiClient);
        builder.Services.AddLogging(services => services.AddConsole().SetMinimumLevel(LogLevel.Trace));

        var kernel = builder.Build();

        // Register plugins.
        kernel.Plugins.AddFromObject(_memoryPlugin);
        kernel.Plugins.AddFromObject(_graphPlugin);
        kernel.Plugins.AddFromObject(_ynabPlugin);
        kernel.Plugins.AddFromObject(_loopbackPlugin);

        // Enable planning.
        var openAIPromptExecutionSettings = new OpenAIPromptExecutionSettings()
        { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() };

        var history = new ChatHistory();

        var timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(session.TimeZone);
        var localTime = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZoneInfo);

        var instructions = $"""
            - You are an AI agent named Ancela.
            - You are a singlular AI instance serving multiple users. 
            - You have a separate chat history for each user, but your memory is
              shared across all users.
            - You communicate with users via SMS so be concise in your responses.
            - Your phone number is '{agentPhoneNumber}'.
            - You are currently chatting with a user whose phone number is '{userPhoneNumber}'.
            - The user's current local date and time is {localTime:f} ({session.TimeZone}).
            - You have the following capabilities:
                1. To-Dos:
                   - You can create, read, update, and delete to-dos for the user.
                   - To-dos are simple text items the user wants to remember to do
                     later.
                   - They are typically action-oriented.
                   - They do not have due dates and you are not able to remind the
                     user proactively.
                   - When a user asks you to 'list my todos', respond with a numbered
                     list of todo titles, oldest first. Always exclude deleted todos.
                2. Knowledge:
                   - You can create, read, update, and delete knowledge entries for the user.
                   - Knowledge entries are pieces of information the user wants you to remember.
                   - They are typically facts or statements.
                3. Calendar Access:
                   - You can read the user's calendar events and create new events.
                4. Email Access:
                   - You can read the user's recent emails and send new emails.
                5. Contact Access:
                  - You can read the user's contacts.
                6. Personal Finance Access:
                   - You can read the user's personal finances via their YNAB account.
                   - You can provide budget summaries and recent transaction information.
                   - You cannot make changes to the user's finances.
                7. Autonomous Behavior:
                   - You can send messages to yourself to enable autonomous behavior.
                   - You can specify a delay in hours before the message is sent.
                   - Use this capability sparingly to break down complex tasks into
                     smaller steps.
            - Don't ask for "anything else?" at the end of your responses.
            - Use the appropriate plugin functions to perform actions related to
              todos, knowledge, calendar, email, contacts, personal finance, and autonomous behavior.
            - Always think step-by-step about how to best assist the user.
            - Always respond in a friendly and helpful manner.            
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
