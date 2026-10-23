using System.ComponentModel;
using System.Text.RegularExpressions;
using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.SemanticKernel.Plugins.RemarkablePlugin;

/// <summary>
/// Semantic Kernel plugin for sending text content to the owner's reMarkable tablet
/// as a PDF document.
/// </summary>
public class RemarkablePlugin(IRemarkableService _remarkableService, IGraphClient _graphClient)
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    [KernelFunction("send_to_remarkable")]
    [Description("Sends text content to the user's reMarkable tablet by converting it to a PDF and uploading it to their reMarkable cloud library. For an email, use send_email_to_remarkable.")]
    public async Task SendToRemarkableAsync(
        [Description("Document title shown on the reMarkable device.")] string title,
        [Description("The text content to render into the PDF body.")] string text)
    {
        await _remarkableService.SendTextAsync(title, text);
    }

    /// <remarks>
    /// The email goes from Graph to the tablet without passing through the model. get_recent_emails
    /// returns only the first 255 characters of each body, so send_to_remarkable could send no more
    /// than the model had: on 2026-10-18 two emails arrived as their title and one line each.
    /// </remarks>
    [KernelFunction("send_email_to_remarkable")]
    [Description("Sends one of the user's emails, whole, to their reMarkable tablet as a PDF titled with its subject. Only when the user asks.")]
    public async Task<SentEmailModel> SendEmailToRemarkableAsync(
        Kernel kernel,
        [Description("The email's id, exactly as returned by get_recent_emails.")] string emailId,
        [Description(EmailPdf.IncludeLinkedImagesDescription)] bool? includeLinkedImages = null)
    {
        if (includeLinkedImages is null && await _graphClient.GetLinkedImageCountAsync(emailId) is > 0 and var linked)
            return new SentEmailModel { Sent = false, LinkedImages = linked, Note = EmailPdf.LinkedImagesQuestion(linked, "Nothing was sent yet.") };

        var email = await _graphClient.RenderEmailAsPdfAsync(emailId, EmailPdf.UserTimeZone(kernel), includeLinkedImages == true);
        var title = string.IsNullOrWhiteSpace(email.Subject) ? "(no subject)" : Whitespace.Replace(email.Subject, " ").Trim();

        using var pdf = new MemoryStream(email.Pdf);
        await _remarkableService.SendPdfAsync(title, pdf);
        return new SentEmailModel
        {
            Sent = true,
            Title = title,
            LinkedImages = email.LinkedImages,
            ImagesMissing = email.ImagesMissing,
        };
    }
}
