namespace Ancela.Agent.SemanticKernel.Plugins.FitbitPlugin;

/// <summary>
/// Fitbit app credentials and the one-time bootstrap seed, read from the environment. Injected as
/// a value (rather than read inline like the other clients) so the token lifecycle is unit-testable
/// without mutating process environment variables.
/// </summary>
public sealed class FitbitCredentials
{
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }

    /// <summary>
    /// The initial refresh token produced by <c>ancela fitbit auth</c>, used once to bootstrap the
    /// Cosmos token document. Ignored thereafter unless its value changes (the re-consent path).
    /// </summary>
    public string? SeedRefreshToken { get; init; }

    public static FitbitCredentials FromEnvironment() => new()
    {
        ClientId = Environment.GetEnvironmentVariable("FITBIT_CLIENT_ID"),
        ClientSecret = Environment.GetEnvironmentVariable("FITBIT_CLIENT_SECRET"),
        SeedRefreshToken = Environment.GetEnvironmentVariable("FITBIT_REFRESH_TOKEN"),
    };
}
