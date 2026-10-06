namespace Ancela.Agent.SemanticKernel.Plugins.UnifiPlugin.Models;

/// <summary>
/// Uptime for an adopted UniFi device. <see cref="UptimeSec"/> is null when the console
/// has no latest statistics for the device (e.g. it's offline).
/// </summary>
public record DeviceUptime(string Name, string? Model, string? State, long? UptimeSec, string? Uptime);
