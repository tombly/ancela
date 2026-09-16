using System.ComponentModel;
using Ancela.Agent.SemanticKernel.Plugins.UnifiPlugin.Models;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.SemanticKernel.Plugins.UnifiPlugin;

/// <summary>
/// Read-only status of the owner's home UniFi network. Both functions are owner-only
/// (see <see cref="KernelProfilePolicy"/>): who is connected at home is presence data.
/// </summary>
public class UnifiPlugin(IUnifiClient _unifiClient)
{
    [KernelFunction("get_network_client_count")]
    [Description("Gets the number of clients (phones, laptops, smart-home devices) currently connected to the owner's home UniFi network.")]
    public async Task<long> GetClientCountAsync()
    {
        return await _unifiClient.GetClientCountAsync();
    }

    [KernelFunction("get_network_uptime")]
    [Description("Gets how long the owner's home network gateway (the UniFi Dream Machine) has been up, with its name, model, and state.")]
    public async Task<DeviceUptime> GetGatewayUptimeAsync()
    {
        return await _unifiClient.GetGatewayUptimeAsync();
    }
}
