using System.Reflection;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.SemanticKernel;

/// <summary>
/// Single source of truth for which functions each <see cref="KernelProfile"/> and each user may
/// use, read from the <see cref="ToolAttribute"/> on every tool. Both enforcement layers consume this:
/// <list type="bullet">
/// <item><see cref="Agent"/> advertises only these functions to the model
/// (<c>FunctionChoiceBehavior.Auto(functions:)</c>) — so denied tools never enter the schema.</item>
/// <item><see cref="AutonomousToolGuardFilter"/> hard-denies any invocation outside this set
/// as defense-in-depth.</item>
/// </list>
/// The autonomous profiles use a <b>default-deny</b> allow-list: a tool runs in one only if its
/// declaration names it. Chat and Onboarding are unrestricted (<c>null</c>), because a human is in
/// the loop — except that within one message, reading email, calendar, the web or another user's
/// text switches sending and instructions off (<see cref="SwitchedOffBy"/>), which only the guard
/// enforces.
///
/// Who may use a tool is a separate axis: the owner may use every tool, and another user only
/// those their grants name (<see cref="MayUse"/>), so a tool is the owner's until a grant names it.
///
/// The autonomous profiles act with no one to review an action first, so they get reads and
/// nothing that sends or writes, with two exceptions for clearing processed email away (see
/// <c>mark_email_read</c>). Email and calendar are left out of StandingRule (anyone can send an
/// email or put text in a meeting invite, and a rule checking a price doesn't need them);
/// ScheduledTask has them, for tasks like a daily calendar summary, and its prompt marks their text
/// as untrusted. <c>web_fetch</c> is in neither: it takes a URL the model, or an injected
/// instruction, chooses, which is a way out for anything it read.
/// </summary>
public static class KernelProfilePolicy
{
    // Every tool's declaration, by name. Read once from Ancela.Agent's [KernelFunction] methods; a
    // function with no [Tool] (one a test builds, say) gets the defaults: the owner's, Chat only.
    private static readonly IReadOnlyDictionary<string, ToolAttribute> Declared = ReadDeclarations();

    private static readonly ToolAttribute Undeclared = new();

    private static Dictionary<string, ToolAttribute> ReadDeclarations()
    {
        var tools = new Dictionary<string, ToolAttribute>(StringComparer.OrdinalIgnoreCase);
        var methods = typeof(KernelProfilePolicy).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly));
        foreach (var method in methods)
        {
            if (method.GetCustomAttribute<KernelFunctionAttribute>() is { } function
                && method.GetCustomAttribute<ToolAttribute>() is { } tool)
                tools.Add(function.Name ?? method.Name, tool); // throws on two tools with one name
        }
        return tools;
    }

    private static ToolAttribute Of(string functionName) =>
        Declared.TryGetValue(functionName, out var tool) ? tool : Undeclared;

    /// <summary>Every declared tool's name, so tests can check each one is loaded and every loaded one declared.</summary>
    public static IEnumerable<string> DeclaredFunctionNames => Declared.Keys;

    private static readonly HashSet<string> StandingRuleAllowed = Names(t => t.Autonomous.HasFlag(Autonomy.StandingRule));
    private static readonly HashSet<string> ScheduledTaskAllowed = Names(t => t.Autonomous.HasFlag(Autonomy.ScheduledTask));

    private static HashSet<string> Names(Func<ToolAttribute, bool> where) =>
        new(Declared.Where(d => where(d.Value)).Select(d => d.Key), StringComparer.OrdinalIgnoreCase);

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

    /// <summary>The grant that gives another user <paramref name="functionName"/>, or None if only the owner may use it.</summary>
    public static ToolGrant GrantOf(string functionName) => Of(functionName).Grant;

    /// <summary>True if no grant gives <paramref name="functionName"/> to anyone but the owner.</summary>
    public static bool IsOwnerOnly(string functionName) => GrantOf(functionName) == ToolGrant.None;

    /// <summary>
    /// True if a user may use <paramref name="functionName"/>: the owner may use every tool, and
    /// anyone else a tool every user has or one their <paramref name="grants"/> name. Enforced
    /// independently of profile: <see cref="Agent"/> advertises only these, and
    /// <see cref="AutonomousToolGuardFilter"/> hard-denies the rest as a backstop.
    /// </summary>
    public static bool MayUse(string functionName, bool isOwner, IReadOnlySet<ToolGrant> grants) =>
        isOwner || GrantOf(functionName) switch
        {
            ToolGrant.None => false,
            ToolGrant.Always => true,
            var grant => grants.Contains(grant),
        };

    /// <summary>True if <paramref name="functionName"/> reads the one email its emailId argument names.</summary>
    public static bool ReadsOneEmail(string functionName) => Of(functionName).Email == OneEmail.Reads;

    /// <summary>
    /// True if, in <paramref name="profile"/>, <paramref name="functionName"/> may act only on an email
    /// the same run has read, with no read of it failing. Applies to the email changes when no person
    /// is there to see them: a report whose attachment failed stays unread where it was, for the
    /// owner to notice. In Chat the user asked for the change and sees the reply.
    /// </summary>
    public static bool NeedsSuccessfulRead(KernelProfile profile, string functionName) =>
        profile is not (KernelProfile.Chat or KernelProfile.Onboarding)
        && Of(functionName).Email == OneEmail.Changes;

    // Outside text: anyone can email the user or put text in a meeting invite, anyone can publish a
    // web page, whoever holds an IP block writes its registry record, and another user writes the
    // to-dos, facts and project entries they save. Once a read of it has run, the model may be
    // acting on instructions planted in it, so for the rest of the message the tools whose OffAfter
    // names it are switched off. This is the one restriction that applies in Chat. A kernel serves
    // one message, so the next message starts clean: the user confirming is what switches them
    // back on. Sending is off after any of it, because a planted instruction pays off by getting
    // something sent; so are instructions, whose text goes into every later Chat prompt.
    // Not covered: text a read put into Ancela's reply carries over in history.

    /// <summary>
    /// The kind of untrusted text <paramref name="functionName"/> returns, or
    /// <see cref="UntrustedSource.None"/> if it returns none. Reads of shared data record
    /// <see cref="UntrustedSource.OtherUsers"/> themselves, only when another user wrote what they return.
    /// </summary>
    public static UntrustedSource UntrustedSourceOf(string functionName) => Of(functionName).Reads;

    /// <summary>
    /// The sources in <paramref name="seen"/> that switch <paramref name="functionName"/> off, or
    /// <see cref="UntrustedSource.None"/> if it may still run.
    /// </summary>
    public static UntrustedSource SwitchedOffBy(string functionName, UntrustedSource seen) =>
        Of(functionName).OffAfter & seen;
}

/// <summary>Where a read's untrusted text comes from (see <see cref="KernelProfilePolicy.UntrustedSourceOf"/>).</summary>
[Flags]
public enum UntrustedSource
{
    None = 0,
    MailOrCalendar = 1,
    Web = 2,
    /// <summary>A to-do, fact or project another user saved: anyone but the owner and the user being served.</summary>
    OtherUsers = 4,
    /// <summary>Every kind: what sending and instructions are switched off after.</summary>
    Any = MailOrCalendar | Web | OtherUsers,
}
