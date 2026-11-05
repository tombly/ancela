using Ancela.Agent.Services;
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

    /// <summary>
    /// The kernel's record of the untrusted content read so far. <see cref="KernelFactory"/> puts
    /// one in every kernel; a kernel built anywhere else gets one on first use.
    /// </summary>
    public static UntrustedContentLog UntrustedContent(Kernel kernel)
    {
        if (kernel.Data.TryGetValue("untrustedContent", out var value) && value is UntrustedContentLog log)
            return log;

        log = new UntrustedContentLog();
        kernel.Data["untrustedContent"] = log;
        return log;
    }

    /// <summary>
    /// Records <see cref="UntrustedSource.OtherUsers"/> when a read returned text one of
    /// <paramref name="authors"/> wrote who is neither the owner nor the user this kernel serves.
    /// Shared data is written by every user with a grant to add to it, and a guest could save a
    /// to-do that reads as an instruction. The owner's own text, and the user's own, is not
    /// outside text to them. An author of null (a project entry saved before entries had one) is
    /// taken to be whoever the caller passes for it.
    /// </summary>
    public static void ReadTextBy(Kernel kernel, OwnerService owner, IEnumerable<string?> authors)
    {
        var user = kernel.Data.TryGetValue("userPhoneNumber", out var u) ? u?.ToString() : null;
        if (authors.Any(a => IsAnotherUser(a, user, owner)))
            UntrustedContent(kernel).Add(UntrustedSource.OtherUsers);
    }

    /// <summary>True if <paramref name="author"/> is someone other than the owner and <paramref name="user"/>.</summary>
    public static bool IsAnotherUser(string? author, string? user, OwnerService owner) =>
        !string.IsNullOrEmpty(author) && author != user && !owner.IsOwner(author);
}
