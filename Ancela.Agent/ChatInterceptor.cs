using Ancela.Agent.Services;
using Microsoft.Extensions.Logging;

namespace Ancela.Agent;

public class ChatInterceptor(
    ILogger<ChatInterceptor> _logger,
    IUserService _userService,
    IAuditLog _auditLog,
    CorrelationContext _correlation,
    Agent _agent)
{
    public async Task<string?> HandleMessage(string message, string userPhoneNumber, string agentPhoneNumber, string[] mediaUrls)
    {
        if (message.Trim().Equals("hello ancela", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Intercepted 'hello' command from {UserPhoneNumber}", userPhoneNumber);

            var existing = await _userService.GetAsync(agentPhoneNumber, userPhoneNumber);
            if (existing != null)
                return "You already have an account.";

            await _userService.CreatePendingAsync(agentPhoneNumber, userPhoneNumber);
            return await _agent.Onboard(message, userPhoneNumber, agentPhoneNumber, mediaUrls);
        }

        if (message.Trim().Equals("goodbye ancela", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Intercepted 'goodbye' command from {UserPhoneNumber}", userPhoneNumber);

            var existing = await _userService.GetAsync(agentPhoneNumber, userPhoneNumber);
            if (existing == null)
                return "You don't have an active account.";

            await _userService.DeleteAsync(agentPhoneNumber, userPhoneNumber);
            await _auditLog.LogAsync(new AuditEntry
            {
                UserPhoneNumber = userPhoneNumber,
                AgentPhoneNumber = agentPhoneNumber,
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = _correlation.Current,
                Actor = "user",
                Category = "session",
                Plugin = nameof(ChatInterceptor),
                Function = "deregister",
                Success = true
            });
            return "Goodbye!";
        }

        var user = await _userService.GetAsync(agentPhoneNumber, userPhoneNumber);

        if (user == null)
        {
            _logger.LogWarning("No account — {UserPhoneNumber} attempted to send a message", userPhoneNumber);
            await _auditLog.LogAsync(new AuditEntry
            {
                UserPhoneNumber = userPhoneNumber,
                AgentPhoneNumber = agentPhoneNumber,
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = _correlation.Current,
                Actor = "user",
                Category = "session",
                Plugin = nameof(ChatInterceptor),
                Function = "message",
                Success = false,
                Error = "no active account"
            });
            return null;
        }

        if (user.Name == null)
            return await _agent.Onboard(message, userPhoneNumber, agentPhoneNumber, mediaUrls);

        return await _agent.Chat(message, userPhoneNumber, agentPhoneNumber, user, mediaUrls);
    }
}
