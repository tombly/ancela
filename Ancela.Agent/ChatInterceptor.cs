using Ancela.Agent.Services;
using Microsoft.Extensions.Logging;

namespace Ancela.Agent;

public class ChatInterceptor(
    ILogger<ChatInterceptor> _logger,
    IUserService _userService,
    IAuditLog _auditLog,
    CorrelationContext _correlation,
    OwnerService _ownerService,
    SmsService _smsService,
    Agent _agent)
{
    public async Task<string?> HandleMessage(string message, string userPhoneNumber, string agentPhoneNumber, string[] mediaUrls)
    {
        var trimmed = message.Trim();
        var isOwner = _ownerService.IsOwner(userPhoneNumber);

        // Access management is owner-only. Non-owners can't run these — the words fall through
        // to normal handling (and an uninvited sender is dropped below).
        if (isOwner)
        {
            if (TryParseCommand(trimmed, "invite", out var inviteTarget))
                return await HandleInvite(inviteTarget, userPhoneNumber, agentPhoneNumber);

            if (TryParseCommand(trimmed, "revoke", out var revokeTarget))
                return await HandleRevoke(revokeTarget, userPhoneNumber, agentPhoneNumber);
        }

        if (trimmed.Equals("hello ancela", StringComparison.OrdinalIgnoreCase))
            return await HandleHello(message, userPhoneNumber, agentPhoneNumber, isOwner, mediaUrls);

        if (trimmed.Equals("goodbye ancela", StringComparison.OrdinalIgnoreCase))
            return await HandleGoodbye(userPhoneNumber, agentPhoneNumber);

        var user = await _userService.GetAsync(agentPhoneNumber, userPhoneNumber);
        if (user == null)
        {
            _logger.LogWarning("No account — {UserPhoneNumber} attempted to send a message", userPhoneNumber);
            await LogSessionAsync(userPhoneNumber, agentPhoneNumber, "message", success: false, error: "no active account");
            return null;
        }

        if (user.Name == null)
            return await _agent.Onboard(message, userPhoneNumber, agentPhoneNumber, mediaUrls);

        return await _agent.Chat(message, userPhoneNumber, agentPhoneNumber, user, mediaUrls);
    }

    private async Task<string?> HandleHello(string message, string userPhoneNumber, string agentPhoneNumber, bool isOwner, string[] mediaUrls)
    {
        _logger.LogInformation("Intercepted 'hello' command from {UserPhoneNumber}", userPhoneNumber);

        var existing = await _userService.GetAsync(agentPhoneNumber, userPhoneNumber);
        if (existing != null)
        {
            // A profile created by an owner invite is pending (Name == null) until onboarded.
            return existing.Name != null
                ? "You already have an account."
                : await _agent.Onboard(message, userPhoneNumber, agentPhoneNumber, mediaUrls);
        }

        // No profile: only the owner may self-register. Everyone else must be invited first.
        if (!isOwner)
        {
            _logger.LogWarning("Uninvited registration attempt from {UserPhoneNumber}", userPhoneNumber);
            await LogSessionAsync(userPhoneNumber, agentPhoneNumber, "register", success: false, error: "not invited");
            return null;  // silent drop — don't confirm the agent exists to an uninvited stranger
        }

        await _userService.CreatePendingAsync(agentPhoneNumber, userPhoneNumber);
        return await _agent.Onboard(message, userPhoneNumber, agentPhoneNumber, mediaUrls);
    }

    private async Task<string> HandleGoodbye(string userPhoneNumber, string agentPhoneNumber)
    {
        _logger.LogInformation("Intercepted 'goodbye' command from {UserPhoneNumber}", userPhoneNumber);

        var existing = await _userService.GetAsync(agentPhoneNumber, userPhoneNumber);
        if (existing == null)
            return "You don't have an active account.";

        await _userService.DeleteAsync(agentPhoneNumber, userPhoneNumber);
        await LogSessionAsync(userPhoneNumber, agentPhoneNumber, "deregister", success: true, error: null);
        return "Goodbye!";
    }

    private async Task<string> HandleInvite(string rawTarget, string ownerPhoneNumber, string agentPhoneNumber)
    {
        _correlation.New();

        var target = NormalizePhoneNumber(rawTarget);
        if (target is null)
            return "Couldn't read that number. Use international format, e.g. +15551234567.";

        if (target == agentPhoneNumber)
            return "That's my own number.";

        var existing = await _userService.GetAsync(agentPhoneNumber, target);
        if (existing != null)
            return $"{target} already has access.";

        await _userService.CreatePendingAsync(agentPhoneNumber, target);
        await _smsService.Send(target, "You've been invited to Ancela. Reply 'hello ancela' to get started.");
        await LogSessionAsync(ownerPhoneNumber, agentPhoneNumber, "invite", success: true, error: null, target: target);
        _logger.LogInformation("Owner invited {Target}", target);
        return $"Invited {target}.";
    }

    private async Task<string> HandleRevoke(string rawTarget, string ownerPhoneNumber, string agentPhoneNumber)
    {
        _correlation.New();

        var target = NormalizePhoneNumber(rawTarget);
        if (target is null)
            return "Couldn't read that number. Use international format, e.g. +15551234567.";

        if (_ownerService.IsOwner(target))
            return "You can't revoke your own access.";

        var existing = await _userService.GetAsync(agentPhoneNumber, target);
        if (existing == null)
            return $"{target} doesn't have access.";

        await _userService.DeleteAsync(agentPhoneNumber, target);
        await LogSessionAsync(ownerPhoneNumber, agentPhoneNumber, "revoke", success: true, error: null, target: target);
        _logger.LogInformation("Owner revoked access for {Target}", target);
        return $"Revoked access for {target}.";
    }

    // Matches "<command> <argument>" case-insensitively, requiring whitespace after the command
    // so "investigate ..." never reads as "invite". Returns the trimmed argument.
    private static bool TryParseCommand(string message, string command, out string argument)
    {
        argument = string.Empty;
        if (!message.StartsWith(command, StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = message[command.Length..];
        if (rest.Length == 0 || !char.IsWhiteSpace(rest[0]))
            return false;

        argument = rest.Trim();
        return argument.Length > 0;
    }

    // Normalizes an owner-typed number to E.164 so it matches the format Twilio delivers in the
    // 'From' field. Requires a leading '+' (country code explicit) to avoid guessing — returns
    // null for anything that isn't a plausible international number.
    private static string? NormalizePhoneNumber(string input)
    {
        var hasPlus = input.TrimStart().StartsWith('+');
        var digits = new string(input.Where(char.IsDigit).ToArray());
        return hasPlus && digits.Length >= 7 ? "+" + digits : null;
    }

    private async Task LogSessionAsync(
        string userPhoneNumber, string agentPhoneNumber, string function, bool success, string? error, string? target = null)
    {
        await _auditLog.LogAsync(new AuditEntry
        {
            UserPhoneNumber = userPhoneNumber,
            AgentPhoneNumber = agentPhoneNumber,
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = _correlation.Current,
            Actor = "user",
            Category = "session",
            Plugin = nameof(ChatInterceptor),
            Function = function,
            Arguments = target,
            Success = success,
            Error = error
        });
    }
}
