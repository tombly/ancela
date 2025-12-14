using Ancilla.FunctionApp.Services;
using Microsoft.Extensions.Logging;

namespace Ancilla.FunctionApp;

/// <summary>
/// Intercepts incoming messages to handle special commands before passing to ChatService.
/// </summary>
public class ChatInterceptor(ILogger<ChatInterceptor> _logger, UserService _userService, ChatService _chatService)
{
    /// <summary>
    /// Processes an incoming message, intercepting special commands or delegating to ChatService.
    /// </summary>
    /// <param name="message">The message content</param>
    /// <param name="from">The sender's phone number</param>
    /// <param name="to">The recipient's phone number (AI's number)</param>
    /// <returns>The response message</returns>
    public async Task<string> HandleMessage(string message, string from, string to)
    {
        // Check if the message is the "start" command (case-insensitive)
        if (message.Trim().Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Intercepted 'start' command from {From}", from);
            
            // Check if user already exists
            var existingUser = await _userService.GetUserAsync(to, from);
            if (existingUser != null)
                return "You're already registered! Send me a message and I'll help you manage your notes.";
            
            // Add the user
            await _userService.CreateUserAsync(to, from);
            return "Welcome! I'm your AI memory assistant. I can help you save and retrieve notes via SMS. Try sending me a note!";
        }

        // Verify user is registered before processing other messages
        var user = await _userService.GetUserAsync(to, from);
        if (user == null)
        {
            _logger.LogWarning("Unregistered user {From} attempted to send message", from);
            return "You need to register first. Please send 'start' to begin.";
        }

        // No interception needed, pass to ChatService
        return await _chatService.Chat(message, from, to);
    }
}