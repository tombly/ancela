using Ancela.Agent.Services;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

#pragma warning disable SKEXP0001

namespace Ancela.Agent;

public class Agent(Kernel _kernel, IChatCompletionService _chatCompletionService, IHistoryService _historyService, CorrelationContext _correlation)
{
    public async Task<string> Chat(string message, string userPhoneNumber, string agentPhoneNumber, UserProfile user, string[] mediaUrls)
    {
        _correlation.New();

        // TODO: Handle media URLs: Save to blob storage with metadata stored by the memory plugin.
        //       Use image analysis to describe images and extract text (store both with metadata).
        //       Allow only images for now.
        //       Need to handle the scenario where there is no message and only media.
        var response = await InvokeModel(message, userPhoneNumber, agentPhoneNumber, user, mediaUrls);
        await _historyService.CreateHistoryEntryAsync(agentPhoneNumber, userPhoneNumber, message, MessageType.User);
        await _historyService.CreateHistoryEntryAsync(agentPhoneNumber, userPhoneNumber, response, MessageType.Agent);
        return response;
    }

    public async Task<string> Onboard(string message, string userPhoneNumber, string agentPhoneNumber, string[] mediaUrls)
    {
        _correlation.New();
        var response = await InvokeOnboarding(message, userPhoneNumber, agentPhoneNumber);
        await _historyService.CreateHistoryEntryAsync(agentPhoneNumber, userPhoneNumber, message, MessageType.User);
        await _historyService.CreateHistoryEntryAsync(agentPhoneNumber, userPhoneNumber, response, MessageType.Agent);
        return response;
    }

    private async Task<string> InvokeModel(string message, string userPhoneNumber, string agentPhoneNumber, UserProfile user, string[] mediaUrls)
    {
        var timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(user.TimeZone!);
        var localTime = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZoneInfo);

        var instructions = $"""
            - You are an AI agent named Ancela.
            - You are a singular AI instance serving multiple users.
            - You have a separate chat history for each user, but your memory is
              shared across all users.
            - You communicate with users via SMS so be concise in your responses.
            - Your phone number is '{agentPhoneNumber}'.
            - You are currently chatting with {user.Name}, whose phone number is '{userPhoneNumber}'.
            - The user's current local date and time is {localTime:f} ({user.TimeZone}).
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
                7. Reminders:
                   - You can schedule a single SMS reminder for the user at a specific
                     absolute time using `create_reminder`.
                   - Reminders are unlike todos: they fire at a chosen time and send a
                     message; todos are passive lists with no scheduling.
                   - When the user gives a relative time ("tomorrow afternoon", "in two
                     hours"), resolve it to an absolute ISO-8601 timestamp using the
                     user's current local date/time and timezone shown above, then ALWAYS
                     confirm the resolved time back to the user in plain language ("Set
                     for tomorrow at 2pm — sound good?"). Never schedule a reminder for
                     a past time.
                   - Use `list_reminders` to show upcoming reminders and `cancel_reminder`
                     to cancel one. To change a reminder, cancel it and create a new one.
                8. SMS:
                   - You can send SMS messages to one or more phone numbers.
                9. Web Access:
                   - You can search the web with `web_search` to look up current information.
                   - You can read the full content of a specific URL with `web_fetch`.
                   - Prefer authoritative sources. For factual claims, search before asserting.
            - Use the appropriate plugin functions to perform actions related to
              todos, knowledge, calendar, email, contacts, personal finance, reminders, and SMS.
            - Always think step-by-step about how to best assist the user.
            - Don't ask for "anything else?" at the end of your responses.
            """;

        var chatHistory = new ChatHistory();
        chatHistory.AddSystemMessage(instructions);

        var historyEntries = await _historyService.GetHistoryAsync(agentPhoneNumber, userPhoneNumber);
        foreach (var entry in historyEntries)
        {
            if (entry.MessageType == MessageType.User)
                chatHistory.AddUserMessage(entry.Content);
            else if (entry.MessageType == MessageType.Agent)
                chatHistory.AddAssistantMessage(entry.Content);
        }

        chatHistory.AddUserMessage(message);

        _kernel.Data["agentPhoneNumber"] = agentPhoneNumber;
        _kernel.Data["userPhoneNumber"] = userPhoneNumber;

        var openAIPromptExecutionSettings = new OpenAIPromptExecutionSettings()
        { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() };

        var modelResponse = await _chatCompletionService.GetChatMessageContentAsync(
            chatHistory,
            executionSettings: openAIPromptExecutionSettings,
            kernel: _kernel
        );

        return modelResponse.ToString();
    }

    private async Task<string> InvokeOnboarding(string message, string userPhoneNumber, string agentPhoneNumber)
    {
        var instructions = """
            You are Ancela, a personal AI assistant that communicates via SMS.
            You are onboarding a new user. Your only goal right now is to collect their name and timezone, then call register_user.
            Start by greeting them and asking for their name.
            Once you have their name, ask what city or region they're in so you can determine their timezone.
            Resolve the city or region to an IANA timezone ID (e.g., 'America/Los_Angeles'), confirm it back to the user in plain language, then call register_user.
            Keep all responses short — this is SMS.
            """;

        var chatHistory = new ChatHistory();
        chatHistory.AddSystemMessage(instructions);

        var historyEntries = await _historyService.GetHistoryAsync(agentPhoneNumber, userPhoneNumber);
        foreach (var entry in historyEntries)
        {
            if (entry.MessageType == MessageType.User)
                chatHistory.AddUserMessage(entry.Content);
            else if (entry.MessageType == MessageType.Agent)
                chatHistory.AddAssistantMessage(entry.Content);
        }

        chatHistory.AddUserMessage(message);

        _kernel.Data["agentPhoneNumber"] = agentPhoneNumber;
        _kernel.Data["userPhoneNumber"] = userPhoneNumber;

        var openAIPromptExecutionSettings = new OpenAIPromptExecutionSettings()
        { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() };

        var modelResponse = await _chatCompletionService.GetChatMessageContentAsync(
            chatHistory,
            executionSettings: openAIPromptExecutionSettings,
            kernel: _kernel
        );

        return modelResponse.ToString();
    }
}
