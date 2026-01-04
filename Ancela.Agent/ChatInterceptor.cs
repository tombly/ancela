using Ancela.Agent.Services;
using Microsoft.Extensions.Logging;

namespace Ancela.Agent;

/// <summary>
/// Intercepts incoming chat messages to handle special commands.
/// </summary>
public class ChatInterceptor(ILogger<ChatInterceptor> _logger, ISessionService _sessionService, Agent _agent)
{
    /// <summary>
    /// Processes an incoming message, intercepting special commands or delegating to the agent.
    /// </summary>
    /// <param name="message">The message content</param>
    /// <param name="userPhoneNumber">The sender's phone number</param>
    /// <param name="agentPhoneNumber">The recipient's phone number (agent's number)</param>
    /// <param name="mediaUrls">List of media URLs attached to the message</param>
    /// <returns>The response message, or null if no session exists</returns>
    public async Task<string?> HandleMessage(string message, string userPhoneNumber, string agentPhoneNumber, string[] mediaUrls)
    {
        // Check if the message is the "hello" command (case-insensitive).
        if (message.Trim().Equals("hello ancela", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Intercepted 'hello' command from {userPhoneNumber}", userPhoneNumber);

            // Check if session already exists.
            var existingSession = await _sessionService.GetSessionAsync(agentPhoneNumber, userPhoneNumber);
            if (existingSession != null)
                return "You have an existing session.";

            // Add the session.
            await _sessionService.CreateSessionAsync(agentPhoneNumber, userPhoneNumber);
            return "Welcome! I'm your AI assistant.";
        }

        // Check if the message is the "goodbye" command (case-insensitive).
        if (message.Trim().Equals("goodbye ancela", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Intercepted 'goodbye' command from {userPhoneNumber}", userPhoneNumber);

            // Check if session exists.
            var existingSession = await _sessionService.GetSessionAsync(agentPhoneNumber, userPhoneNumber);
            if (existingSession == null)
                return "You don't have an active session.";

            // Delete the session (but keep any todos).
            await _sessionService.DeleteSessionAsync(agentPhoneNumber, userPhoneNumber);
            return "Goodbye! Your session has been ended. Your todos have been preserved. Send 'hello ancela' to start a new session.";
        }

        // Verify session is registered before processing other messages.
        var session = await _sessionService.GetSessionAsync(agentPhoneNumber, userPhoneNumber);
        if (session == null)
        {
            _logger.LogWarning("No session - {userPhoneNumber} attempted to send message", userPhoneNumber);
            return null;
        }

        // No interception needed, pass to agent.
        return await _agent.Chat(message, userPhoneNumber, agentPhoneNumber, session, mediaUrls);
    }
}
