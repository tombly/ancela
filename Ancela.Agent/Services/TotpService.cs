using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Ancela.Agent.Services;

/// <summary>
/// Time-based One-Time Password (TOTP, RFC 6238) for owner step-up. This is a deliberately
/// small, dependency-free implementation of the same scheme Google Authenticator / Authy use:
/// a shared Base32 secret plus the current 30-second time window, run through HMAC-SHA1 and
/// truncated to six digits. Both sides compute the code independently — nothing secret crosses
/// the wire — so it works fully offline.
///
/// It exists to add a second factor on top of the SMS sender number, which is the only identity
/// signal the agent otherwise has and is spoofable. The owner's secret comes from the
/// <c>OWNER_TOTP_SECRET</c> environment variable; when that is unset the feature is simply
/// inactive (<see cref="IsConfigured"/> is false) and callers fall back to prior behavior.
///
/// Caveat: a code presented over SMS is visible to Twilio and the carriers, so this defends
/// against an attacker who spoofs the owner's number (they still lack the secret), not against
/// one who can read the owner's texts. It is opt-in hardening, not hardware MFA.
/// </summary>
public class TotpService
{
    private const int Step = 30;        // seconds per code window (the authenticator default)
    private const int Digits = 6;       // code length
    private const int Window = 1;       // accept the adjacent windows too, to tolerate clock skew

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"; // RFC 4648

    private readonly string? _ownerSecret;

    public TotpService()
    {
        _ownerSecret = Environment.GetEnvironmentVariable("OWNER_TOTP_SECRET")?.Trim();
    }

    /// <summary>True when an owner secret is configured. When false, step-up is inactive.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_ownerSecret);

    /// <summary>
    /// Verifies a code the owner presented against the configured secret at the current time.
    /// Always false when no secret is configured — callers gate on <see cref="IsConfigured"/>
    /// first, so an unconfigured instance never silently "passes". A malformed (non-Base32)
    /// configured secret fails <b>closed</b> here (returns false) rather than throwing out of
    /// message handling: the gate stays on and the owner sees a normal "didn't match", instead
    /// of a typo'd secret either crashing the queue processor or silently disabling step-up.
    /// </summary>
    public virtual bool VerifyOwnerCode(string code)
    {
        if (!IsConfigured)
            return false;
        try
        {
            return Verify(_ownerSecret!, code, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Computes the TOTP code for a secret at a given Unix time. Pure and deterministic — the
    /// seam used by tests (RFC 6238 vectors) and by <see cref="Verify"/>.
    /// </summary>
    public static string ComputeCode(string base32Secret, long unixSeconds, int step = Step, int digits = Digits)
    {
        var key = Base32Decode(base32Secret);
        long counter = unixSeconds / step;

        Span<byte> message = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(message, counter);

        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(key, message, hash);

        // Dynamic truncation (RFC 4226 §5.3): the low nibble of the last byte picks a 4-byte
        // window; mask the high bit to stay positive, then take the low `digits` decimal places.
        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24)
                   | ((hash[offset + 1] & 0xFF) << 16)
                   | ((hash[offset + 2] & 0xFF) << 8)
                   | (hash[offset + 3] & 0xFF);

        int otp = binary % (int)Math.Pow(10, digits);
        return otp.ToString().PadLeft(digits, '0');
    }

    /// <summary>
    /// True if <paramref name="code"/> matches the secret within +/- one time window. The window
    /// tolerance absorbs small clock differences and a code typed as it rolls over. Uses a
    /// fixed-time comparison so a near-miss can't be teased out by timing.
    /// </summary>
    public static bool Verify(string base32Secret, string code, long unixSeconds,
        int window = Window, int step = Step, int digits = Digits)
    {
        if (string.IsNullOrWhiteSpace(code))
            return false;

        var presented = Encoding.ASCII.GetBytes(code.Trim());
        for (int i = -window; i <= window; i++)
        {
            var candidate = Encoding.ASCII.GetBytes(ComputeCode(base32Secret, unixSeconds + (i * step), step, digits));
            if (CryptographicOperations.FixedTimeEquals(candidate, presented))
                return true;
        }
        return false;
    }

    /// <summary>Generates a fresh random Base32 secret (default 160 bits, per RFC 4226).</summary>
    public static string GenerateSecret(int bytes = 20) => Base32Encode(RandomNumberGenerator.GetBytes(bytes));

    /// <summary>
    /// Builds the <c>otpauth://</c> URI an authenticator app scans from a QR code. This is the
    /// entire payload of the QR: the Base32 secret plus the labels and algorithm parameters.
    /// </summary>
    public static string BuildOtpAuthUri(string base32Secret, string issuer = "Ancela", string account = "owner")
    {
        // The label is "issuer:account" with a literal colon separator; the two components are
        // percent-encoded individually (RFC 6238 / Key Uri Format), not the colon between them.
        var label = $"{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}";
        var escapedIssuer = Uri.EscapeDataString(issuer);
        return $"otpauth://totp/{label}?secret={base32Secret}&issuer={escapedIssuer}&algorithm=SHA1&digits={Digits}&period={Step}";
    }

    private static string Base32Encode(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder((data.Length + 4) / 5 * 8);
        int buffer = 0, bitsLeft = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                builder.Append(Base32Alphabet[(buffer >> bitsLeft) & 0x1F]);
            }
        }
        if (bitsLeft > 0)
            builder.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
        return builder.ToString();
    }

    private static byte[] Base32Decode(string input)
    {
        var bytes = new List<byte>(input.Length * 5 / 8);
        int buffer = 0, bitsLeft = 0;
        foreach (var c in input)
        {
            if (c is ' ' or '-' or '=')
                continue; // tolerate the grouping/padding people copy out of authenticator apps
            int value = Base32Alphabet.IndexOf(char.ToUpperInvariant(c));
            if (value < 0)
                throw new FormatException($"'{c}' is not a valid Base32 character.");
            buffer = (buffer << 5) | value;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                bytes.Add((byte)((buffer >> bitsLeft) & 0xFF));
            }
        }
        return [.. bytes];
    }
}
