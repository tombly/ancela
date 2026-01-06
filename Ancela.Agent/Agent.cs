using Ancela.Agent.SemanticKernel.Plugins.PlanningPlugin;
using Ancela.Agent.Services;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Ancela.Agent;

public class Agent(Kernel _kernel, IChatCompletionService chatCompletionService, IHistoryService _historyService, PlanningPlugin _planningPlugin)
{
    public async Task<string> Chat(string message, string userPhoneNumber, string agentPhoneNumber, SessionEntry session, string[] mediaUrls)
    {
        // TODO: Handle media URLs: Save to blob storage with metadata stored by the memory plugin.
        //       Use image analysis to describe images and extract text (store both with metadata).
        //       Allow only images for now.
        //       Need to handle the scenario where there is no message and only media.  
        var response = await InvokeModel(message, userPhoneNumber, agentPhoneNumber, session, mediaUrls);
        await _historyService.CreateHistoryEntryAsync(agentPhoneNumber, userPhoneNumber, message, MessageType.User);
        await _historyService.CreateHistoryEntryAsync(agentPhoneNumber, userPhoneNumber, response, MessageType.Agent);
        return response;
    }

    public async Task PerformNextStepInPlan(Guid planId, string userPhoneNumber, string agentPhoneNumber)
    {
        // TODO: Pass the history into InvokeModel() so that it can properly be
        //       included in the planning context.
        var planHistory = await _planningPlugin.GetPlanHistory(planId, agentPhoneNumber);
        var historySection = planHistory.Length == 0
            ? "No previous plan history."
            : "Plan history:\n" + string.Join("\n", planHistory.Select((entry, index) => $"{index + 1}. {entry}"));

        var response = await InvokeModel($"""
              - Perform the next step in the plan with ID {planId}.
              - Mark the step as completed.
              - Check if the plan has any incomplete steps, and if so, schedule its execution based on the defined delay.
              - Use the existing plan history to maintain continuity.
              - Provide a brief summary of the actions taken.
              - Current plan history context:
                {historySection}
              """, userPhoneNumber, agentPhoneNumber, new SessionEntry { TimeZone = "UTC" }, Array.Empty<string>());

        await _planningPlugin.SaveToPlanHistory(planId, agentPhoneNumber, response);
    }

    public async Task<string> InvokeModel(string message, string userPhoneNumber, string agentPhoneNumber, SessionEntry session, string[] mediaUrls)
    {
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
                7. Planning:
                   - You can create plans with ordered steps to accomplish complex or
                     scheduled tasks.
                   - Each step includes a delay (in hours) that indicates how long to
                     wait before executing the step (after the previous step is completed).
                8. SMS:
                   - You can send SMS messages to one or more phone numbers.
            - Use the appropriate plugin functions to perform actions related to
              todos, knowledge, calendar, email, contacts, personal finance, planning, and SMS.
            - Always think step-by-step about how to best assist the user.
            - Don't ask for "anything else?" at the end of your responses.
            """;

        var chatHistory = new ChatHistory();
        chatHistory.AddSystemMessage(instructions);

        // Load chat history from database. Not sure if this is appropriate when planning.
        var historyEntries = await _historyService.GetHistoryAsync(agentPhoneNumber, userPhoneNumber);
        foreach (var entry in historyEntries)
        {
            if (entry.MessageType == MessageType.User)
                chatHistory.AddUserMessage(entry.Content);
            else if (entry.MessageType == MessageType.Agent)
                chatHistory.AddAssistantMessage(entry.Content);
        }

        chatHistory.AddUserMessage(message);

        // Populate kernel arguments with contextual data
        _kernel.Data["agentPhoneNumber"] = agentPhoneNumber;
        _kernel.Data["userPhoneNumber"] = userPhoneNumber;

        // Enable planning.
        var openAIPromptExecutionSettings = new OpenAIPromptExecutionSettings()
        { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() };

        var modelResponse = await chatCompletionService.GetChatMessageContentAsync(
            chatHistory,
            executionSettings: openAIPromptExecutionSettings,
            kernel: _kernel
        );

        var response = modelResponse.ToString();
        return response;
    }
}
