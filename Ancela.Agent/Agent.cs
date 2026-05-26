using Ancela.Agent.SemanticKernel.Plugins.ScheduledTaskPlugin.Models;
using Ancela.Agent.SemanticKernel.Plugins.StandingRulePlugin;
using Ancela.Agent.SemanticKernel.Plugins.StandingRulePlugin.Models;
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
                10. Standing Rules:
                   - A standing rule is a recurring condition you watch over time and notify
                     the user about when it becomes true, e.g. "let me know if the Cync patio
                     lights go on sale" or "tell me when I'm due for a haircut".
                   - This is different from a reminder: a reminder fires once at a fixed,
                     known time; a standing rule is evaluated repeatedly on an interval and
                     only notifies the user when the watched condition is actually met.
                   - Use `create_standing_rule` with a clear description, an evaluation
                     interval in hours (minimum 1), and a notification cooldown in days.
                   - Use `list_standing_rules`, `pause_standing_rule`, `resume_standing_rule`,
                     and `delete_standing_rule` to manage them.
                   - Choose reminders for "remind me at/on <time>"; choose standing rules for
                     "let me know if/when <condition>".
                11. Scheduled Tasks:
                   - A scheduled task runs on a recurring clock schedule and sends the user a
                     freshly generated message each time, e.g. "send me a summary of my
                     calendar each morning" or "text me my budget every Friday at 5pm".
                   - Distinguish the three recurring/timed tools:
                       * Reminder — one-time message at a single fixed moment.
                       * Standing rule — a condition watched over time; notifies only when met.
                       * Scheduled task — an action that re-runs on a clock schedule and always
                         reports back.
                   - Use `create_scheduled_task` with a description of what to do, a local time
                     of day ("HH:mm" in the user's timezone), and the days to run ("daily",
                     "weekdays", "weekends", or specific days like "Mon,Wed,Fri"). When the user
                     says "each morning"/"every evening", pick a sensible time and confirm it back.
                   - Use `list_scheduled_tasks`, `pause_scheduled_task`, `resume_scheduled_task`,
                     and `delete_scheduled_task` to manage them.
            - Use the appropriate plugin functions to perform actions related to
              todos, knowledge, calendar, email, contacts, personal finance, reminders,
              standing rules, scheduled tasks, and SMS.
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

        return await InvokeKernelAsync(chatHistory, agentPhoneNumber, userPhoneNumber);
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

        return await InvokeKernelAsync(chatHistory, agentPhoneNumber, userPhoneNumber);
    }

    /// <summary>
    /// Evaluates a standing rule out-of-band (queue-triggered, no chat history). The agent
    /// decides whether the rule's condition warrants notifying the user and, if so and the
    /// cooldown allows, sends the SMS itself via the SMS plugin. The returned
    /// <see cref="StandingRuleEvaluation"/> captures whether it notified and its reasoning.
    /// </summary>
    public async Task<StandingRuleEvaluation> EvaluateStandingRule(StandingRule rule, UserProfile user)
    {
        // The caller (StandingRuleQueueProcessor) opens the correlation scope so the decision
        // audit row and any tool-call audit rows from this evaluation share one correlation ID.
        var timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(user.TimeZone!);
        var localTime = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZoneInfo);

        // Cooldown is enforced deterministically here, not left to the model: notifications
        // are blocked until NotificationCooldownDays have elapsed since the last one.
        var notifyAllowed = rule.LastNotifiedAt is null
            || DateTimeOffset.UtcNow - rule.LastNotifiedAt.Value >= TimeSpan.FromDays(rule.NotificationCooldownDays);

        var instructions = $"""
            You are Ancela, an AI assistant evaluating a STANDING RULE on behalf of a user.
            This is a background evaluation, not a conversation. The user is not waiting on a reply.

            User: {user.Name} ({rule.UserPhoneNumber}). Current local time: {localTime:f} ({user.TimeZone}).
            Your phone number: '{rule.AgentPhoneNumber}'.

            The standing rule to evaluate:
            "{rule.Description}"

            Your job:
            - Determine whether the rule's condition is currently met and warrants notifying the user.
            - You may use your read-only tools (web search/fetch, memory, calendar, contacts, finances) to investigate.
              Prefer authoritative sources (manufacturers, named retailers); corroborate before concluding.
            - Notifications currently allowed: {(notifyAllowed ? "YES" : $"NO (cooldown active — last notified {rule.LastNotifiedAt:O})")}.
            - If, and only if, action is warranted AND notifications are allowed: send a concise SMS to the user
              with the `send_sms` tool to their number, then begin your final reply with the token "NOTIFIED:".
            - Otherwise, take no action and begin your final reply with the token "NO_ACTION:".
            - After the token, briefly explain your reasoning. This explanation is recorded for audit.
            - Do not schedule reminders or create other rules. Do not send more than one SMS.
            """;

        var chatHistory = new ChatHistory();
        chatHistory.AddSystemMessage(instructions);
        chatHistory.AddUserMessage("Evaluate the standing rule now.");

        var response = await InvokeKernelAsync(chatHistory, rule.AgentPhoneNumber, rule.UserPhoneNumber);

        var notified = response.TrimStart().StartsWith("NOTIFIED:", StringComparison.OrdinalIgnoreCase);
        return new StandingRuleEvaluation { Notified = notified, Reasoning = response };
    }

    /// <summary>
    /// Carries out a recurring scheduled task out-of-band (queue-triggered, no chat history)
    /// and returns the SMS message text for the caller to send. Unlike a standing rule, this
    /// always produces a message — there is no condition to evaluate.
    /// </summary>
    public async Task<string> PerformScheduledTask(ScheduledTask task, UserProfile user)
    {
        // The caller (ScheduledTaskQueueProcessor) opens the correlation scope.
        var timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(user.TimeZone!);
        var localTime = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZoneInfo);

        var instructions = $"""
            You are Ancela, an AI assistant carrying out a recurring SCHEDULED TASK for a user.
            This runs in the background on a schedule; the user is not in a live conversation.

            User: {user.Name} ({task.UserPhoneNumber}). Current local time: {localTime:f} ({user.TimeZone}).
            Your phone number: '{task.AgentPhoneNumber}'.

            The scheduled task to perform now:
            "{task.Description}"

            Your job:
            - Carry out the task using your tools (calendar, email, contacts, finances, web, memory) as needed.
            - Produce a single concise SMS message conveying the result to the user.
            - Output ONLY the message text to send. Do not add greetings, sign-offs, or commentary
              about the task itself, and do not call the SMS tool yourself — the message you return is sent.
            - If there is genuinely nothing to report, return a brief note saying so.
            - Do not create reminders, rules, or other scheduled tasks.
            """;

        var chatHistory = new ChatHistory();
        chatHistory.AddSystemMessage(instructions);
        chatHistory.AddUserMessage("Perform the scheduled task now.");

        return await InvokeKernelAsync(chatHistory, task.AgentPhoneNumber, task.UserPhoneNumber);
    }

    private async Task<string> InvokeKernelAsync(ChatHistory chatHistory, string agentPhoneNumber, string userPhoneNumber)
    {
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
