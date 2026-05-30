using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Remarkable.Api.Client;

namespace Ancela.Agent.SemanticKernel.Plugins.RemarkablePlugin;

public interface IRemarkableService
{
    Task<string> SendTextAsync(string visibleName, string text, CancellationToken cancellationToken = default);
}

/// <summary>
/// Renders text to a PDF with QuestPDF and uploads it to the owner's reMarkable cloud
/// library. Session tokens are refreshed lazily from the long-lived device token
/// (env var <c>REMARKABLE_DEVICE_TOKEN</c>) and cached in-process until they near expiry.
/// </summary>
public sealed class RemarkableService(IHttpClientFactory _httpClientFactory) : IRemarkableService
{
    // reMarkable session tokens last roughly one hour; refresh well before that.
    private static readonly TimeSpan SessionTokenLifetime = TimeSpan.FromMinutes(50);

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _sessionToken;
    private DateTimeOffset _sessionTokenExpiresAt;

    public async Task<string> SendTextAsync(string visibleName, string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(visibleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var client = new RemarkableClient(_httpClientFactory.CreateClient("remarkable"));
        var sessionToken = await GetSessionTokenAsync(client, cancellationToken).ConfigureAwait(false);

        using var pdf = RenderPdf(visibleName, text);
        var result = await client.UploadPdfAsync(sessionToken, visibleName, pdf, cancellationToken).ConfigureAwait(false);
        return result.DocumentId;
    }

    private async Task<string> GetSessionTokenAsync(RemarkableClient client, CancellationToken cancellationToken)
    {
        if (_sessionToken is not null && DateTimeOffset.UtcNow < _sessionTokenExpiresAt)
            return _sessionToken;

        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessionToken is not null && DateTimeOffset.UtcNow < _sessionTokenExpiresAt)
                return _sessionToken;

            var deviceToken = Environment.GetEnvironmentVariable("REMARKABLE_DEVICE_TOKEN");
            if (string.IsNullOrWhiteSpace(deviceToken))
                throw new InvalidOperationException("REMARKABLE_DEVICE_TOKEN is not configured.");

            _sessionToken = await client.RefreshSessionAsync(deviceToken, cancellationToken).ConfigureAwait(false);
            _sessionTokenExpiresAt = DateTimeOffset.UtcNow.Add(SessionTokenLifetime);
            return _sessionToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static MemoryStream RenderPdf(string title, string body)
    {
        var stream = new MemoryStream();
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(2, Unit.Centimetre);
                page.DefaultTextStyle(t => t.FontSize(12));

                page.Header().Text(title).SemiBold().FontSize(18);
                page.Content().PaddingTop(12).Text(body);
                page.Footer().AlignRight().Text(t =>
                {
                    t.CurrentPageNumber();
                    t.Span(" / ");
                    t.TotalPages();
                });
            });
        }).GeneratePdf(stream);

        stream.Position = 0;
        return stream;
    }
}
