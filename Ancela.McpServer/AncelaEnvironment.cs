namespace Ancela.McpServer;

/// <summary>
/// Configuration for the MCP server, read from the environment because Claude Desktop launches it
/// as a bare subprocess — there is no appsettings or user-secrets context to lean on.
/// </summary>
public sealed class AncelaEnvironment
{
    /// <summary>
    /// The agent's own number, which is the Cosmos partition key for every container. Named
    /// ANCELA_AGENT_PHONE_NUMBER here; the deployed app calls the same value TWILIO_PHONE_NUMBER,
    /// so either is accepted.
    /// </summary>
    public string AgentPhoneNumber { get; } =
        Environment.GetEnvironmentVariable("ANCELA_AGENT_PHONE_NUMBER")
        ?? Environment.GetEnvironmentVariable("TWILIO_PHONE_NUMBER")
        ?? throw new InvalidOperationException(
            "Set ANCELA_AGENT_PHONE_NUMBER to the agent's phone number — it is the Cosmos partition key.");

    /// <summary>Same precedence as the CLI: explicit endpoint, else derived from the prefix.</summary>
    public static string? ResolveCosmosEndpoint()
    {
        var explicitEndpoint = Environment.GetEnvironmentVariable("ANCELA_COSMOS_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(explicitEndpoint))
            return explicitEndpoint;

        var prefix = Environment.GetEnvironmentVariable("ANCELA_RESOURCE_PREFIX");
        return string.IsNullOrWhiteSpace(prefix)
            ? null
            : $"https://{prefix}-cosmos.documents.azure.com:443/";
    }
}
