namespace Ancela.Agent.SemanticKernel.Plugins.GoogleHealthPlugin;

/// <summary>
/// Google OAuth client credentials and the one-time bootstrap seed, read from the environment.
/// Injected as a value (rather than read inline) so the token lifecycle is unit-testable without
/// mutating process environment variables.
/// </summary>
public sealed class GoogleHealthCredentials
{
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }

    /// <summary>
    /// The initial refresh token produced by <c>ancela health auth</c>, used once to bootstrap the
    /// Cosmos token document. Ignored thereafter unless its value changes (the re-consent path).
    /// </summary>
    public string? SeedRefreshToken { get; init; }

    public static GoogleHealthCredentials FromEnvironment() => new()
    {
        ClientId = Environment.GetEnvironmentVariable("GOOGLE_HEALTH_CLIENT_ID"),
        ClientSecret = Environment.GetEnvironmentVariable("GOOGLE_HEALTH_CLIENT_SECRET"),
        SeedRefreshToken = Environment.GetEnvironmentVariable("GOOGLE_HEALTH_REFRESH_TOKEN"),
    };
}
