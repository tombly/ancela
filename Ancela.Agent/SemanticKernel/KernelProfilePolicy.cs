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
/// unrestricted (<c>null</c>), because a human is in the loop — except that within one message,
/// reading email, calendar or the web switches sending and instructions off
/// (<see cref="SwitchedOffBy"/>), which only the guard enforces.
/// </summary>
public static class KernelProfilePolicy
{
    // Read-only functions for StandingRule evaluation. Email and calendar content are
    // excluded: both are untrusted input channels (anyone can send an email or inject
    // text into a meeting invite), and standing rules don't need them to check external
    // conditions (prices, news, etc.). web_fetch is excluded because it takes an
    // attacker-controllable URL — a direct exfiltration channel for any injected content.
    private static readonly HashSet<string> StandingRuleAllowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "web_search",
        "get_todos", "get_knowledge",
        "get_contacts", "get_contact_by_name",
        "get_accounts", "get_categories", "get_month_summaries",
        "get_transactions", "get_scheduled_transactions",
        // Safe here where web_fetch is not: the forecast reads one fixed government API rather
        // than a URL the model (or an injected instruction) can choose.
        "get_forecast",
    };

    // Read-only functions for ScheduledTask execution. Email and calendar are included
    // because tasks like "daily calendar summary" or "email digest" legitimately need
    // them. The system prompt explicitly marks their content as untrusted data.
    // web_fetch is excluded because it takes an attacker-controllable URL — a direct
    // exfiltration channel for any injected content.
    private static readonly HashSet<string> ScheduledTaskAllowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "web_search",
        "get_todos", "get_knowledge",
        "get_calendar_events", "get_recent_emails", "get_contacts", "get_contact_by_name",
        "get_accounts", "get_categories", "get_month_summaries",
        "get_transactions", "get_scheduled_transactions",
        "get_daily_activity", "get_sleep_summary", "get_resting_heart_rate",
        "get_forecast",
    };

    /// <summary>
    /// The functions a profile may use, or <c>null</c> for unrestricted profiles
    /// (Chat, Onboarding) where every loaded function is allowed.
    /// </summary>
    public static IReadOnlySet<string>? AllowedFunctions(KernelProfile profile) => profile switch
    {
        KernelProfile.Chat or KernelProfile.Onboarding => null,
        KernelProfile.StandingRule => StandingRuleAllowed,
        KernelProfile.ScheduledTask => ScheduledTaskAllowed,
        // Every enum member is listed above, so this arm is only reached by a value that is not
        // a defined KernelProfile — a cast integer, or a member added without updating this
        // switch. It denies everything rather than returning null, because null means
        // "unrestricted" and an unrecognized profile is the last place to grant that. A boxed
        // `(KernelProfile)999` satisfies a `is KernelProfile` type check, so callers cannot be
        // relied on to screen these out; closing it here covers both consumers at once.
        _ => NoneAllowed,
    };

    /// <summary>Deny-all list for profiles this policy does not recognize.</summary>
    private static readonly HashSet<string> NoneAllowed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True if <paramref name="functionName"/> may be invoked under <paramref name="profile"/>.
    /// Unrestricted profiles allow everything; restricted profiles allow only their list.
    /// </summary>
    public static bool IsAllowed(KernelProfile profile, string functionName)
    {
        var allowed = AllowedFunctions(profile);
        return allowed is null || allowed.Contains(functionName);
    }

    // Functions that act on the owner's shared resources, send on the agent's behalf, or
    // read owner-private data (Google Health data, the diagnostics self-check — which reveals
    // operational/security posture — and home network status, which is presence data).
    // Non-owner users are denied access even in the otherwise-unrestricted Chat profile.
    // This is an axis orthogonal to the profile allow-lists above — both are enforced.
    // save_email_to_onedrive writes into the owner's OneDrive and send_email_to_remarkable onto
    // the owner's tablet; both are in neither allow-list.
    private static readonly HashSet<string> OwnerOnlyFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "send_sms", "send_email", "create_calendar_event", "send_to_remarkable", "save_email_to_onedrive",
        "send_email_to_remarkable",
        "get_daily_activity", "get_sleep_summary", "get_resting_heart_rate",
        "check_services", "check_anomalies",
        "get_network_client_count", "get_network_uptime",
    };

    /// <summary>
    /// True if <paramref name="functionName"/> may only be invoked by the owner (covers both
    /// send/write actions and owner-private reads such as health data). Enforced independently
    /// of profile: <see cref="Agent"/> drops these from the advertised set for non-owners, and
    /// <see cref="AutonomousToolGuardFilter"/> hard-denies them as a backstop.
    /// </summary>
    public static bool IsOwnerOnly(string functionName) => OwnerOnlyFunctions.Contains(functionName);

    // Reads whose results carry text that people other than the user write: anyone can email
    // the user or put text in a meeting invite, and anyone can publish a web page. Once one of
    // these has run, the model may be acting on instructions planted in that text, so for the
    // rest of the message the functions below are switched off. This is the one restriction
    // that applies in Chat. A kernel serves one message, so the next message starts clean: the
    // user confirming is what switches them back on.
    private static readonly Dictionary<string, UntrustedSource> UntrustedReads = new(StringComparer.OrdinalIgnoreCase)
    {
        ["get_recent_emails"] = UntrustedSource.MailOrCalendar,
        ["get_calendar_events"] = UntrustedSource.MailOrCalendar,
        ["web_search"] = UntrustedSource.Web,
        ["web_fetch"] = UntrustedSource.Web,
    };

    // What each source switches off. Sending is off after either, because a planted
    // instruction pays off by getting something sent (forward the inbox, text this number).
    // Instructions are off after either because remember_instruction's text goes into every
    // later Chat prompt, which would carry an injection into messages that never read it;
    // forget_instruction is off so one can't remove a rule the user set. web_fetch is off after
    // mail and calendar because a URL the model picks is itself a way out (?q=<the inbox>), but
    // stays on after a search, since opening a result is what searching is for. Left on:
    // save_email_to_onedrive, send_email_to_remarkable and create_calendar_event, which write only
    // into the owner's own folder, tablet and calendar (there is no attendees parameter, so no
    // invitation goes out).
    // Not covered: text a read put into Ancela's reply carries over in history, and knowledge
    // or to-dos saved after a read are not marked when read back later.
    private static readonly Dictionary<string, UntrustedSource> SwitchedOffAfter = new(StringComparer.OrdinalIgnoreCase)
    {
        ["send_email"] = UntrustedSource.MailOrCalendar | UntrustedSource.Web,
        ["send_sms"] = UntrustedSource.MailOrCalendar | UntrustedSource.Web,
        ["remember_instruction"] = UntrustedSource.MailOrCalendar | UntrustedSource.Web,
        ["forget_instruction"] = UntrustedSource.MailOrCalendar | UntrustedSource.Web,
        ["web_fetch"] = UntrustedSource.MailOrCalendar,
    };

    /// <summary>
    /// The kind of untrusted text <paramref name="functionName"/> returns, or
    /// <see cref="UntrustedSource.None"/> if it returns none.
    /// </summary>
    public static UntrustedSource UntrustedSourceOf(string functionName) =>
        UntrustedReads.TryGetValue(functionName, out var source) ? source : UntrustedSource.None;

    /// <summary>
    /// The sources in <paramref name="seen"/> that switch <paramref name="functionName"/> off, or
    /// <see cref="UntrustedSource.None"/> if it may still run.
    /// </summary>
    public static UntrustedSource SwitchedOffBy(string functionName, UntrustedSource seen) =>
        SwitchedOffAfter.TryGetValue(functionName, out var sources) ? sources & seen : UntrustedSource.None;

    /// <summary>Every function named by the untrusted-content rules, so tests can check each one exists.</summary>
    public static IEnumerable<string> UntrustedContentFunctionNames => UntrustedReads.Keys.Concat(SwitchedOffAfter.Keys);
}

/// <summary>Where a read's untrusted text comes from (see <see cref="KernelProfilePolicy.UntrustedSourceOf"/>).</summary>
[Flags]
public enum UntrustedSource
{
    None = 0,
    MailOrCalendar = 1,
    Web = 2,
}
