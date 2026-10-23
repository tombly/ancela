using Microsoft.SemanticKernel;

namespace Ancela.Agent.SemanticKernel;

/// <summary>Reads what <see cref="Agent"/> puts in a kernel's data for the plugins.</summary>
public static class KernelData
{
    /// <summary>
    /// The user's time zone (<c>userTimeZone</c>, set on every kernel that serves a user), or UTC
    /// when it is missing or unknown. The server runs in UTC, so without it "today" turns into
    /// tomorrow on a Pacific evening.
    /// </summary>
    public static TimeZoneInfo UserTimeZone(Kernel kernel) =>
        kernel.Data.TryGetValue("userTimeZone", out var id) && id is string tz
            && TimeZoneInfo.TryFindSystemTimeZoneById(tz, out var found)
            ? found
            : TimeZoneInfo.Utc;
}
