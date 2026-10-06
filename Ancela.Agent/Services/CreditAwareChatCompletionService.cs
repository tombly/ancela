using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Ancela.Agent.Services;

/// <summary>
/// Wraps the real chat completion service with the out-of-credit breaker.
///
/// This is the single chokepoint: <see cref="Agent"/> is the only consumer of
/// <see cref="IChatCompletionService"/>, so decorating it covers every path — chat, onboarding,
/// standing-rule evaluation and scheduled tasks — without each queue processor having to know
/// about credit at all.
/// </summary>
public sealed class CreditAwareChatCompletionService(
    IChatCompletionService _inner,
    ModelAvailability _availability,
    ICreditAlertService _alerts) : IChatCompletionService
{
    public IReadOnlyDictionary<string, object?> Attributes => _inner.Attributes;

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        await ShortCircuitIfOutOfFundsAsync(nameof(GetChatMessageContentsAsync));

        try
        {
            var result = await _inner.GetChatMessageContentsAsync(chatHistory, executionSettings, kernel, cancellationToken);
            _availability.RecordSuccess();
            return result;
        }
        catch (Exception ex) when (CreditAlertService.IsCreditExhausted(ex))
        {
            _availability.RecordOutOfFunds();
            await _alerts.NotifyIfPendingAsync(nameof(GetChatMessageContentsAsync));
            throw new ModelOutOfFundsException(ex);
        }
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await ShortCircuitIfOutOfFundsAsync(nameof(GetStreamingChatMessageContentsAsync));

        // Enumerated manually so a mid-stream failure is classified the same way as a failure on
        // the first call; a try/catch cannot wrap a yield.
        var stream = _inner.GetStreamingChatMessageContentsAsync(chatHistory, executionSettings, kernel, cancellationToken);
        await using var enumerator = stream.GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            bool moved;
            try
            {
                moved = await enumerator.MoveNextAsync();
            }
            catch (Exception ex) when (CreditAlertService.IsCreditExhausted(ex))
            {
                _availability.RecordOutOfFunds();
                await _alerts.NotifyIfPendingAsync(nameof(GetStreamingChatMessageContentsAsync));
                throw new ModelOutOfFundsException(ex);
            }

            if (!moved)
                break;

            yield return enumerator.Current;
        }

        _availability.RecordSuccess();
    }

    /// <summary>
    /// Blocks the call when the balance is known empty. Still asks for a notification: if the
    /// transition text failed to send, every blocked call is another chance to deliver it.
    /// </summary>
    private async Task ShortCircuitIfOutOfFundsAsync(string source)
    {
        if (!_availability.ShouldShortCircuit())
            return;

        await _alerts.NotifyIfPendingAsync(source);
        throw new ModelOutOfFundsException();
    }
}
