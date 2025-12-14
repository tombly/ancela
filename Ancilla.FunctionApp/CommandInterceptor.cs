using Ancilla.FunctionApp.Services;
using Microsoft.Extensions.Logging;

namespace Ancilla.FunctionApp;

/// <summary>
/// Intercepts incoming messages to handle special commands before passing to ChatService.
/// </summary>
public class CommandInterceptor(ILogger<CommandInterceptor> _logger, SessionService _sessionService, ChatService _chatService)
{
    /// <summary>
    /// Processes an incoming message, intercepting special commands or delegating to ChatService.
    /// </summary>
    /// <param name="message">The message content</param>
    /// <param name="userPhoneNumber">The sender's phone number</param>
    /// <param name="agentPhoneNumber">The recipient's phone number (agent's number)</param>
    /// <returns>The response message, or null if no session exists</returns>
    public async Task<string?> HandleMessage(string message, string userPhoneNumber, string agentPhoneNumber)
    {
        // Check if the message is the "hello" command (case-insensitive).
        if (message.Trim().Equals("hello ancilla", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Intercepted 'hello' command from {userPhoneNumber}", userPhoneNumber);

            // Check if session already exists.
            var existingSession = await _sessionService.GetSessionAsync(agentPhoneNumber, userPhoneNumber);
            if (existingSession != null)
                return "You have an existing session.";

            // Add the session.
            await _sessionService.CreateSessionAsync(agentPhoneNumber, userPhoneNumber);
            return "Welcome! I'm your AI memory assistant. I can help you save and retrieve notes via SMS. Try sending me a note!";
        }

        // Check if the message is the "goodbye" command (case-insensitive).
        if (message.Trim().Equals("goodbye ancilla", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Intercepted 'goodbye' command from {userPhoneNumber}", userPhoneNumber);

            // Check if session exists.
            var existingSession = await _sessionService.GetSessionAsync(agentPhoneNumber, userPhoneNumber);
            if (existingSession == null)
                return "You don't have an active session.";

            // Delete the session (but keep any notes).
            await _sessionService.DeleteSessionAsync(agentPhoneNumber, userPhoneNumber);
            return "Goodbye! Your session has been ended. Your notes have been preserved. Send 'hello ancilla' to start a new session.";
        }

        // Verify session is registered before processing other messages.
        var session = await _sessionService.GetSessionAsync(agentPhoneNumber, userPhoneNumber);
        if (session == null)
        {
            _logger.LogWarning("No session - {userPhoneNumber} attempted to send message", userPhoneNumber);
            return null;
        }

        // No interception needed, pass to ChatService.
        return await _chatService.Chat(message, userPhoneNumber, agentPhoneNumber, session);
    }
}