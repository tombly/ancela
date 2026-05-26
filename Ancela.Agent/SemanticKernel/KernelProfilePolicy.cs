namespace Ancela.Agent.SemanticKernel;

/// <summary>
/// Single source of truth for which functions each <see cref="KernelProfile"/> may use.
/// Both enforcement layers consume this:
/// <list type="bullet">
/// <item><see cref="Agent"/> advertises only these functions to the model
/// (<c>FunctionChoiceBehavior.Auto(functions:)</c>) — so denied tools never enter the schema.</item>
/// <item><see cref="AutonomousToolGuardFilter"/> hard-denies any invocation outside this set
/// as defense-in-depth.</item>
/// </list>
/// The autonomous profiles use a <b>default-deny</b> allow-list: anything not explicitly
/// listed (including any tool added later) is denied automatically. Chat and Onboarding are
/// unrestricted (<c>null</c>), because a human is in the loop.
/// </summary>
public static class KernelProfilePolicy
{
    // Read-only functions for StandingRule evaluation. Email and calendar content are
    // excluded: both are untrusted input channels (anyone can send an email or inject
    // text into a meeting invite), and standing rules don't need them to check external
    // conditions (prices, news, etc.).
    private static readonly HashSet<string> StandingRuleAllowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "web_search", "web_fetch",
        "get_todos", "get_knowledge",
        "get_contacts", "get_contact_by_name",
        "get_accounts", "get_categories", "get_month_summaries",
    };

    // Read-only functions for ScheduledTask execution. Email and calendar are included
    // because tasks like "daily calendar summary" or "email digest" legitimately need
    // them. The system prompt explicitly marks their content as untrusted data.
    private static readonly HashSet<string> ScheduledTaskAllowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "web_search", "web_fetch",
        "get_todos", "get_knowledge",
        "get_calendar_events", "get_recent_emails", "get_contacts", "get_contact_by_name",
        "get_accounts", "get_categories", "get_month_summaries",
    };

    /// <summary>
    /// The functions a profile may use, or <c>null</c> for unrestricted profiles
    /// (Chat, Onboarding) where every loaded function is allowed.
    /// </summary>
    public static IReadOnlySet<string>? AllowedFunctions(KernelProfile profile) => profile switch
    {
        KernelProfile.StandingRule => StandingRuleAllowed,
        KernelProfile.ScheduledTask => ScheduledTaskAllowed,
        _ => null,
    };

    /// <summary>
    /// True if <paramref name="functionName"/> may be invoked under <paramref name="profile"/>.
    /// Unrestricted profiles allow everything; restricted profiles allow only their list.
    /// </summary>
    public static bool IsAllowed(KernelProfile profile, string functionName)
    {
        var allowed = AllowedFunctions(profile);
        return allowed is null || allowed.Contains(functionName);
    }
}
