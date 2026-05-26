namespace Ancela.Agent.Services;

/// <summary>
/// Identifies the single owner of this agent. The owner is the person whose connected
/// accounts (email, calendar, finances) the agent acts on. Ownership is derived from the
/// <c>OWNER_PHONE_NUMBER</c> configuration and never stored per-user — comparing a sender's
/// number to it is the whole check. Owner-only functions (sending SMS/email, writing the
/// owner's calendar) are gated on this; other registered users get read-only access.
/// </summary>
public class OwnerService
{
    private readonly string _ownerPhoneNumber;

    public OwnerService()
    {
        _ownerPhoneNumber = Environment.GetEnvironmentVariable("OWNER_PHONE_NUMBER")
            ?? throw new Exception("OWNER_PHONE_NUMBER not set");
    }

    public string OwnerPhoneNumber => _ownerPhoneNumber;

    public bool IsOwner(string? userPhoneNumber) =>
        !string.IsNullOrWhiteSpace(userPhoneNumber)
        && string.Equals(userPhoneNumber, _ownerPhoneNumber, StringComparison.Ordinal);
}
