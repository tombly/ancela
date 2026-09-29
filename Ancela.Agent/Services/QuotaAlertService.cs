using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.Services;

public interface IQuotaAlertService
{
    /// <summary>
    /// Texts the owner if <paramref name="exception"/> is OpenAI refusing the call for lack of
    /// credit. Any other exception is ignored. Never throws: alerting must not mask the failure
    /// it reports.
    /// </summary>
    Task NotifyIfCreditExhaustedAsync(Exception exception, string agentPhoneNumber, string source);
}

/// <summary>
/// Tells the owner when OpenAI rejects calls because the account is out of credit.
///
/// This is a backstop, not a warning: OpenAI exposes no remaining-balance endpoint to an API
/// key, so the first signal Ancela can act on is the failure itself. Set a balance threshold
/// notification in the OpenAI dashboard for advance warning.
///
/// Two properties make this work when the agent does not:
/// <list type="bullet">
/// <item>The alert needs only Twilio — no model call, no tool, no kernel — because it has to
/// fire precisely when model access is dead. The body is a fixed string for the same reason.</item>
/// <item>The send is throttled to one message per <see cref="Window"/> per agent, since every
/// failing queue message calls this. The September 2026 exhaustion produced ~5 failures a day
/// for 9 days; unthrottled that is a text per failure.</item>
/// </list>
/// </summary>
public class QuotaAlertService(
    IQuotaAlertStore _store,
    SmsService _smsService,
    OwnerService _ownerService,
    TimeProvider _timeProvider,
    ILogger<QuotaAlertService> _logger) : IQuotaAlertService
{
    /// <summary>One alert per day while the balance stays empty — a daily nag, not a flood.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private const string AlertBody =
        "Ancela is out of OpenAI credit. Replies and scheduled tasks will keep failing until the "
        + "balance is topped up: https://platform.openai.com/settings/organization/billing";

    // OpenAI returns 429 both for ordinary rate limiting (transient, retry works) and for an
    // empty balance (permanent until paid). Only these markers separate the two, so match on
    // them rather than on the status code.
    private static readonly string[] Markers = ["insufficient_quota", "credit_balance_exhausted"];

    /// <summary>
    /// True if this exception, or anything it wraps, is OpenAI reporting an exhausted balance.
    /// </summary>
    public static bool IsCreditExhausted(Exception? exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is AggregateException aggregate
                && aggregate.InnerExceptions.Any(IsCreditExhausted))
            {
                return true;
            }

            if (ContainsMarker(ex.Message))
                return true;

            // Semantic Kernel keeps the provider's raw body here; the marker is not always
            // promoted into the exception message.
            if (ex is HttpOperationException http && ContainsMarker(http.ResponseContent))
                return true;
        }

        return false;
    }

    public async Task NotifyIfCreditExhaustedAsync(Exception exception, string agentPhoneNumber, string source)
    {
        if (!IsCreditExhausted(exception))
            return;

        try
        {
            if (!await _store.TryClaimAsync(agentPhoneNumber, _timeProvider.GetUtcNow(), Window))
            {
                _logger.LogInformation(
                    "OpenAI credit exhausted in {Source}; owner already notified within {Window}.",
                    source, Window);
                return;
            }

            await _smsService.Send(_ownerService.OwnerPhoneNumber, AlertBody);
            _logger.LogWarning("OpenAI credit exhausted in {Source}; owner notified.", source);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send the OpenAI credit-exhaustion alert for {Source}.", source);
        }
    }

    private static bool ContainsMarker(string? text) =>
        text is not null && Markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
