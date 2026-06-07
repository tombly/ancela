using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Ancela.Agent.SemanticKernel.Plugins.FitbitPlugin.Models;

/// <summary>
/// A persisted OAuth 2.0 token for a third-party provider. Single-owner: one document per
/// provider, with <see cref="Provider"/> as both the document id and the partition key.
///
/// The refresh token is single-use and rotates on every refresh, so this document — not the
/// environment seed — is the mutable source of truth once bootstrapped. <see cref="SeedFingerprint"/>
/// records which env seed the document was bootstrapped from; a mismatch triggers re-bootstrap.
/// </summary>
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public class OAuthToken
{
    /// <summary>Cosmos document id; carries the same value as <see cref="Provider"/>.</summary>
    public required string Id { get; set; }

    /// <summary>Provider key, e.g. "fitbit" — also the partition key.</summary>
    public required string Provider { get; set; }

    public required string AccessToken { get; set; }
    public required string RefreshToken { get; set; }
    public required DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>SHA-256 of the env seed refresh token this document was bootstrapped from.</summary>
    public string? SeedFingerprint { get; set; }

    /// <summary>
    /// Cosmos ETag, carried in-memory for optimistic-concurrency writes. Transport metadata, not
    /// document data, so it is not serialized into the stored document.
    /// </summary>
    [JsonIgnore]
    public string? ETag { get; set; }
}
