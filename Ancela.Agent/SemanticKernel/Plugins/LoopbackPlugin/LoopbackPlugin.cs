using System.ComponentModel;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.SemanticKernel.Plugins.ChatPlugin;

/// <summary>
/// Provides functions for an agent to send messages to itself so that it can
/// have autonomous behavior without external user input.
/// </summary>
public class LoopbackPlugin(ILoopbackService _loopbackService)
{
    [KernelFunction("send_message_to_self")]
    [Description("Sends a message back to the agent itself to enable autonomous behavior.")]
    public async Task SendMessageToSelf(
        Kernel kernel,
        [Description("The phone number of the user the message is in regard to")]
        string userPhoneNumber,
        [Description("The phone number of the agent receiving the message")]
        string agentPhoneNumber,
        [Description("The content of the message to send")]
        string content,
        [Description("Optional delay in hours (decimal) before the message is sent. If not specified or 0, the message is sent immediately.")]
        decimal? delayHours = null)
    {
        await _loopbackService.SendMessageToSelf(userPhoneNumber, agentPhoneNumber, content, delayHours);
    }
}
