using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.Services;

public interface ICreditAlertService
{
    /// <summary>
    /// Tells the owner the balance is empty, once per outage. Safe to call on every blocked call —
    /// it is a no-op once the owner has been told, and retries if a previous send failed. Never
    /// throws: alerting must not mask the failure it reports.
    /// </summary>
    Task NotifyIfPendingAsync(string source);
}

/// <summary>
/// Texts the owner when OpenAI rejects calls for lack of credit.
///
/// A backstop, not a warning: OpenAI exposes no remaining-balance endpoint to an API key (the
/// documented costs API needs an admin key and reports spend, not balance), so the failure is the
/// first signal available. Set a balance threshold notification in the OpenAI dashboard for
/// advance warning.
///
/// The send needs only Twilio — no model call, no tool, no kernel — because it has to work
/// precisely when model access is dead, and the body is a fixed string for the same reason.
/// <see cref="ModelAvailability"/> decides whether this is the transition, so volume is one text
/// per outage however many scheduled tasks or hourly standing rules are configured.
/// </summary>
public class CreditAlertService(
    ModelAvailability _availability,
    SmsService _smsService,
    OwnerService _ownerService,
    ILogger<CreditAlertService> _logger) : ICreditAlertService
{
    private const string OwnerBody =
        "Ancela is out of OpenAI credit. Replies and scheduled tasks will keep failing until the "
        + "balance is topped up: https://platform.openai.com/settings/organization/billing";

    // OpenAI returns 429 both for ordinary rate limiting (transient, retry works) and for an empty
    // balance (permanent until paid). Only these markers separate the two, so match on them rather
    // than on the status code.
    private static readonly string[] Markers = ["insufficient_quota", "credit_balance_exhausted"];

    /// <summary>True if this exception, or anything it wraps, is OpenAI reporting an empty balance.</summary>
    public static bool IsCreditExhausted(Exception? exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is AggregateException aggregate && aggregate.InnerExceptions.Any(IsCreditExhausted))
                return true;

            if (ContainsMarker(ex.Message))
                return true;

            // Semantic Kernel keeps the provider's raw body here; the marker is not always promoted
            // into the exception message.
            if (ex is HttpOperationException http && ContainsMarker(http.ResponseContent))
                return true;
        }

        return false;
    }

    public async Task NotifyIfPendingAsync(string source)
    {
        if (!_availability.TryClaimNotification())
        {
            _logger.LogInformation("Out of OpenAI credit in {Source}; owner already notified.", source);
            return;
        }

        try
        {
            await _smsService.Send(_ownerService.OwnerPhoneNumber, OwnerBody);
            _availability.NotificationSucceeded();
            _logger.LogWarning("Out of OpenAI credit in {Source}; owner notified.", source);
        }
        catch (Exception ex)
        {
            _availability.NotificationFailed();
            _logger.LogError(ex, "Failed to notify the owner of OpenAI credit exhaustion from {Source}.", source);
        }
    }

    private static bool ContainsMarker(string? text) =>
        text is not null && Markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
