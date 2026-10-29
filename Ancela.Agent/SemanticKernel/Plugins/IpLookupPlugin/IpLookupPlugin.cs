using System.ComponentModel;
using Ancela.Agent.SemanticKernel.Plugins.IpLookupPlugin.Models;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.SemanticKernel.Plugins.IpLookupPlugin;

public class IpLookupPlugin(IRdapClient _rdapClient)
{
    [KernelFunction("lookup_ip")]
    [Description("Looks up who holds an IP address in the internet registries (ARIN, RIPE, APNIC, LACNIC, AFRINIC): "
        + "the network's name, the organizations named on it, its country and address range. Use it to find out "
        + "whose server an address in an email, a report or an alert belongs to.")]
    public async Task<IpRegistration?> LookupIpAsync(
        [Description("One IPv4 or IPv6 address, e.g. 209.85.220.41. Not a host name.")] string ipAddress)
    {
        return await _rdapClient.LookupAsync(ipAddress);
    }
}
