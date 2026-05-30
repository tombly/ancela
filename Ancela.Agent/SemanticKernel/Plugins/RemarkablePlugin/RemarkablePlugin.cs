using System.ComponentModel;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.SemanticKernel.Plugins.RemarkablePlugin;

/// <summary>
/// Semantic Kernel plugin for sending text content to the owner's reMarkable tablet
/// as a PDF document.
/// </summary>
public class RemarkablePlugin(IRemarkableService _remarkableService)
{
    [KernelFunction("send_to_remarkable")]
    [Description("Sends text content to the user's reMarkable tablet by converting it to a PDF and uploading it to their reMarkable cloud library.")]
    public async Task SendToRemarkableAsync(
        [Description("Document title shown on the reMarkable device.")] string title,
        [Description("The text content to render into the PDF body.")] string text)
    {
        await _remarkableService.SendTextAsync(title, text);
    }
}
