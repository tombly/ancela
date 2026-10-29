namespace Ancela.Agent.SemanticKernel.Plugins.IpLookupPlugin.Models;

/// <summary>
/// An IP address's record in the internet registries: the block it belongs to and who holds it. The
/// registries don't agree on where the holder goes. ARIN names it as the registrant; APNIC often
/// gives only contacts and a description such as "China Telecom", and AFRINIC only contacts, such as
/// "Hetzner IP Admin". So everything that can name the holder is returned and the model reads it.
/// </summary>
/// <param name="Address">The address looked up, written out in full.</param>
/// <param name="Network">The block's name, e.g. "GOOGLE", "AWSEMAIL-Z" or "CHINANET-JS".</param>
/// <param name="Range">The block's first and last address.</param>
/// <param name="Country">The two-letter country the block is registered in, when the registry gives one.</param>
/// <param name="Description">The holder's own description of the block, its first few lines.</param>
/// <param name="Contacts">The organizations and roles named on the block, e.g. "Google LLC" as registrant.</param>
public record IpRegistration(
    string Address,
    string? Network,
    string? Range,
    string? Country,
    string[] Description,
    RegistryContact[] Contacts);

/// <summary>A name on a registry record and what it is there as: registrant, administrative, technical, abuse.</summary>
public record RegistryContact(string Name, string[] Roles);
