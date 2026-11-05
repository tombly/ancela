namespace Ancela.Agent.SemanticKernel;

/// <summary>
/// Who may use a tool and what it touches, declared on its <c>[KernelFunction]</c> method.
/// <see cref="KernelProfilePolicy"/> reads these, and <c>SecurityTests</c> fails for a loaded tool
/// without one, so a new tool has to say where it stands. Left at the defaults, a tool is the
/// owner's alone, runs only in Chat, and reads nothing other people write.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ToolAttribute : Attribute
{
    /// <summary>The grant that gives another user this tool; <see cref="ToolGrant.None"/> keeps it the owner's.</summary>
    public ToolGrant Grant { get; set; }

    /// <summary>The autonomous profiles that may use it. None by default: those profiles are default-deny.</summary>
    public Autonomy Autonomous { get; set; }

    /// <summary>The kind of text other people write that its result carries.</summary>
    public UntrustedSource Reads { get; set; }

    /// <summary>The kinds of outside text that, once read, switch it off for the rest of a message.</summary>
    public UntrustedSource OffAfter { get; set; }

    /// <summary>Whether it reads or changes the one email its emailId argument names.</summary>
    public OneEmail Email { get; set; }
}

/// <summary>
/// What the owner can give another user, by text: <c>grant &lt;number&gt; &lt;grants…&gt; &lt;code&gt;</c>.
/// Each names the tools it covers on the tools themselves (<see cref="ToolAttribute.Grant"/>).
/// </summary>
public enum ToolGrant
{
    /// <summary>Not grantable: the owner's alone.</summary>
    None,
    /// <summary>Every registered user has it: their own instructions, and signing up.</summary>
    Always,
    Todos,
    Knowledge,
    Projects,
    Budget,
    Calendar,
    Contacts,
    Web,
    Reminders,
    Automation,
}

/// <summary>The autonomous profiles a tool may run in (<see cref="ToolAttribute.Autonomous"/>).</summary>
[Flags]
public enum Autonomy
{
    None = 0,
    StandingRule = 1,
    ScheduledTask = 2,
    Both = StandingRule | ScheduledTask,
}

/// <summary>How a tool deals with the one email its emailId argument names (<see cref="ToolAttribute.Email"/>).</summary>
public enum OneEmail
{
    None,
    /// <summary>A read that throws counts against the email: an autonomous run won't mark it read or move it.</summary>
    Reads,
    /// <summary>In an autonomous run, only on an email the run has read with no read of it failing.</summary>
    Changes,
}

/// <summary>Grant names as the owner texts them and as a user's profile stores them.</summary>
public static class ToolGrants
{
    /// <summary>The grants the owner can give: every <see cref="ToolGrant"/> but None and Always.</summary>
    public static IReadOnlyList<ToolGrant> Grantable { get; } =
        [.. Enum.GetValues<ToolGrant>().Where(g => g is not (ToolGrant.None or ToolGrant.Always))];

    public static string Name(ToolGrant grant) => grant.ToString().ToLowerInvariant();

    /// <summary>Reads one grant as the owner typed it ("todos", "Budget"), or false if it isn't one.</summary>
    public static bool TryParse(string word, out ToolGrant grant) =>
        Enum.TryParse(word, ignoreCase: true, out grant)
        && word.All(char.IsLetter)                    // Enum.TryParse also takes "3"
        && Grantable.Contains(grant);

    /// <summary>The grants a profile holds. A name that is no longer a grant is skipped.</summary>
    public static IReadOnlySet<ToolGrant> Of(IEnumerable<string> names) =>
        names.Select(n => TryParse(n, out var grant) ? grant : ToolGrant.None)
            .Where(g => g != ToolGrant.None)
            .ToHashSet();
}
