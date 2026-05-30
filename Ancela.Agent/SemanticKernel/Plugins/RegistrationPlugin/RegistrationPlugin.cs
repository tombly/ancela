using System.ComponentModel;
using Ancela.Agent.Services;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.SemanticKernel.Plugins.RegistrationPlugin;

public class RegistrationPlugin(IUserService _userService, IAuditLog _auditLog, CorrelationContext _correlation)
{
    [KernelFunction("register_user")]
    [Description("Completes user registration by saving their name, timezone, and home location. Call only after confirming all three with the user.")]
    public async Task<string> RegisterUserAsync(Kernel kernel,
        [Description("The user's preferred name.")] string name,
        [Description("IANA timezone ID, e.g. 'America/Los_Angeles'. Resolve a city or region name to an IANA ID.")] string timeZone,
        [Description("The user's home city/region in human-readable form, e.g. 'Seattle, WA'. Use the wording the user gave, not the IANA ID. This is the default location for queries like weather.")] string location)
    {
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString() ?? string.Empty;
        var userPhoneNumber = kernel.Data["userPhoneNumber"]?.ToString() ?? string.Empty;

        try { TimeZoneInfo.FindSystemTimeZoneById(timeZone); }
        catch
        {
            return $"'{timeZone}' is not a recognized IANA timezone. Try something like 'America/Los_Angeles' or 'America/New_York'.";
        }

        await _userService.CompleteRegistrationAsync(agentPhoneNumber, userPhoneNumber, name, timeZone, location);

        await _auditLog.LogAsync(new AuditEntry
        {
            UserPhoneNumber = userPhoneNumber,
            AgentPhoneNumber = agentPhoneNumber,
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = _correlation.Current,
            Actor = "user",
            Category = "session",
            Plugin = nameof(RegistrationPlugin),
            Function = "register",
            Success = true
        });

        return "Registration complete.";
    }
}
