using Ancela.Agent.Services;
using FluentAssertions;

namespace Ancela.Agent.Tests;

/// <summary>
/// Verifies the TOTP implementation against the RFC 6238 Appendix B reference vectors and exercises
/// the verification window, enrollment helpers, and owner-secret configuration. Pure math — no
/// OpenAI key or network needed.
/// </summary>
public class TotpServiceTests
{
    // RFC 6238 reference secret: ASCII "12345678901234567890" encoded as Base32.
    private const string RfcSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    // RFC 6238 Appendix B (SHA1) TOTP values, truncated to the low 6 digits.
    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void ComputeCode_MatchesRfc6238Vectors(long unixSeconds, string expected)
    {
        TotpService.ComputeCode(RfcSecret, unixSeconds).Should().Be(expected);
    }

    [Fact]
    public void Verify_AcceptsCurrentCode()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var code = TotpService.ComputeCode(RfcSecret, now);

        TotpService.Verify(RfcSecret, code, now).Should().BeTrue();
    }

    [Fact]
    public void Verify_AcceptsAdjacentWindow()
    {
        // A code from the previous 30s window is still accepted (clock-skew tolerance).
        const long now = 1111111109L;
        var previous = TotpService.ComputeCode(RfcSecret, now - 30);

        TotpService.Verify(RfcSecret, previous, now).Should().BeTrue();
    }

    [Fact]
    public void Verify_RejectsCodeOutsideWindow()
    {
        const long now = 1111111109L;
        var stale = TotpService.ComputeCode(RfcSecret, now - 120);

        TotpService.Verify(RfcSecret, stale, now).Should().BeFalse();
    }

    [Fact]
    public void Verify_RejectsEmptyCode()
    {
        TotpService.Verify(RfcSecret, "", DateTimeOffset.UtcNow.ToUnixTimeSeconds()).Should().BeFalse();
    }

    [Fact]
    public void GenerateSecret_RoundTripsThroughVerify()
    {
        var secret = TotpService.GenerateSecret();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var code = TotpService.ComputeCode(secret, now);

        TotpService.Verify(secret, code, now).Should().BeTrue();
    }

    [Fact]
    public void BuildOtpAuthUri_ContainsSecretAndParameters()
    {
        var uri = TotpService.BuildOtpAuthUri("JBSWY3DPEHPK3PXP", issuer: "Ancela", account: "owner");

        uri.Should().StartWith("otpauth://totp/Ancela:owner?");
        uri.Should().Contain("secret=JBSWY3DPEHPK3PXP");
        uri.Should().Contain("issuer=Ancela");
        uri.Should().Contain("algorithm=SHA1");
        uri.Should().Contain("digits=6");
        uri.Should().Contain("period=30");
    }

    [Fact]
    public void VerifyOwnerCode_IsFalseWhenUnconfigured()
    {
        Environment.SetEnvironmentVariable("OWNER_TOTP_SECRET", null);
        var service = new TotpService();

        service.IsConfigured.Should().BeFalse();
        service.VerifyOwnerCode("000000").Should().BeFalse();
    }

    [Fact]
    public void VerifyOwnerCode_FailsClosedOnMalformedSecret()
    {
        // '0'/'1'/'8'/'9' are not in the Base32 alphabet. A typo'd secret must deny gracefully
        // (gate stays on), not throw out of message handling and not silently disable step-up.
        Environment.SetEnvironmentVariable("OWNER_TOTP_SECRET", "not-valid-base32-0189");
        try
        {
            var service = new TotpService();

            service.IsConfigured.Should().BeTrue();
            service.Invoking(s => s.VerifyOwnerCode("123456")).Should().NotThrow();
            service.VerifyOwnerCode("123456").Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("OWNER_TOTP_SECRET", null);
        }
    }

    [Fact]
    public void VerifyOwnerCode_AcceptsCurrentCodeWhenConfigured()
    {
        var secret = TotpService.GenerateSecret();
        Environment.SetEnvironmentVariable("OWNER_TOTP_SECRET", secret);
        try
        {
            var service = new TotpService();
            var code = TotpService.ComputeCode(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            service.IsConfigured.Should().BeTrue();
            service.VerifyOwnerCode(code).Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("OWNER_TOTP_SECRET", null);
        }
    }
}
