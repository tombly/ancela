using Azure.Core;
using Azure.Identity;

namespace Ancela.Cli.Infrastructure;

/// <summary>
/// The credential every command signs in with: your az login (or another developer sign-in),
/// never a managed identity. DefaultAzureCredential first probes for an Azure VM's identity at
/// 169.254.169.254; on some networks (plane Wi-Fi, 2026-10-23) that address times out instead of
/// refusing, and `ancela chat "…"` took 3m18s for an 8-second reply, or the probe failed outright
/// without falling back to az login. A laptop never has a managed identity, so skipping it costs
/// nothing. One shared instance, so Cosmos and Service Bus share its token cache.
/// </summary>
public static class CliCredential
{
    public static readonly TokenCredential Instance = new DefaultAzureCredential(new DefaultAzureCredentialOptions
    {
        ExcludeManagedIdentityCredential = true,
        ExcludeWorkloadIdentityCredential = true,
    });
}
