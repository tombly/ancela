using System.ComponentModel;
using Ancela.Agent.Services;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.SemanticKernel.Plugins.SmsPlugin;

/// <summary>
/// Semantic Kernel plugin for sending SMS messages through the configured SMS service.
/// </summary>
public class SmsPlugin(SmsService _smsService)
{
    [KernelFunction("send_sms")]
    [Description("Sends an SMS message to one or more comma-separated phone numbers.")]
    public async Task SendSmsAsync(
      [Description("Comma-separated list of destination phone numbers in E.164 format.")] string phoneNumbers,
      [Description("Message body to send. Will be truncated to SMS length if needed.")] string message)
    {
        if (string.IsNullOrWhiteSpace(phoneNumbers))
            throw new ArgumentException("phoneNumbers is required", nameof(phoneNumbers));
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("message is required", nameof(message));

        await _smsService.Send(phoneNumbers, message);
    }
}
