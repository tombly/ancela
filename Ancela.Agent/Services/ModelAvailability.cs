namespace Ancela.Agent.Services;

/// <summary>
/// Thrown in place of calling the model when the OpenAI balance is known to be empty. A distinct
/// type so callers can tell "no credit" (permanent until paid, don't retry) apart from an ordinary
/// model failure (worth retrying).
/// </summary>
public sealed class ModelOutOfFundsException(Exception? inner = null)
    : Exception("The OpenAI account is out of credit; the model was not called.", inner);

/// <summary>
/// A circuit breaker for the model: once OpenAI reports an empty balance, calls short-circuit
/// instead of being retried, and the owner is told once — on the transition, not on every failure.
///
/// Held in memory rather than Cosmos. The function app runs at <c>maxReplicas 1</c>, so there is no
/// second instance to disagree with; a restart simply closes the breaker, and the next failure
/// re-trips it and re-notifies. That costs an occasional extra text and buys no container, no
/// stored state, and no clock governing how often the owner hears about it.
///
/// A closed breaker is the normal state. Open, it lets one call through every
/// <see cref="HalfOpenAfter"/> to find out whether the balance was topped up — the only way to
/// learn that, since a short-circuited call never reaches OpenAI. The interval is short because a
/// rejected call is free, recovery should be near-immediate once paid, and a misclassified failure
/// then locks the agent out for minutes rather than days.
/// </summary>
public sealed class ModelAvailability(TimeProvider _timeProvider)
{
    public static readonly TimeSpan HalfOpenAfter = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();

    private bool _open;
    private DateTimeOffset _lastAttempt;
    private bool _ownerNotified;
    private bool _notificationInFlight;

    /// <summary>True while the balance is known empty and it is not yet time to re-probe.</summary>
    public bool ShouldShortCircuit()
    {
        lock (_gate)
            return _open && _timeProvider.GetUtcNow() - _lastAttempt < HalfOpenAfter;
    }

    /// <summary>A model call succeeded: close the breaker and re-arm notification.</summary>
    public void RecordSuccess()
    {
        lock (_gate)
        {
            _open = false;
            _ownerNotified = false;
            _notificationInFlight = false;
        }
    }

    /// <summary>
    /// A model call actually reached OpenAI and was refused for lack of credit: open the breaker
    /// and start the half-open countdown. Deliberately separate from
    /// <see cref="TryClaimNotification"/> — a short-circuited call must be able to retry a failed
    /// notification without pushing the recovery probe further away each time.
    /// </summary>
    public void RecordOutOfFunds()
    {
        lock (_gate)
        {
            _open = true;
            _lastAttempt = _timeProvider.GetUtcNow();
        }
    }

    /// <summary>
    /// Claims the right to tell the owner: true once per outage, to one caller at a time. Release
    /// it with <see cref="NotificationSucceeded"/> or <see cref="NotificationFailed"/>.
    /// </summary>
    public bool TryClaimNotification()
    {
        lock (_gate)
        {
            if (_ownerNotified || _notificationInFlight)
                return false;

            _notificationInFlight = true;
            return true;
        }
    }

    /// <summary>The owner was told. Nothing more is sent until a model call succeeds again.</summary>
    public void NotificationSucceeded()
    {
        lock (_gate)
        {
            _ownerNotified = true;
            _notificationInFlight = false;
        }
    }

    /// <summary>
    /// Telling the owner failed. Releases the claim so the next failure tries again — otherwise a
    /// single Twilio hiccup at the moment of transition would swallow the only notification.
    /// </summary>
    public void NotificationFailed()
    {
        lock (_gate)
            _notificationInFlight = false;
    }
}

/// <summary>
/// What a user gets back when they text while the balance is empty.
/// </summary>
public static class OutOfFundsMessages
{
    public const string BillingUrl = "https://platform.openai.com/settings/organization/billing";

    /// <summary>
    /// The owner gets the cause and the fix; other authorized users get neither. Billing state is
    /// operational detail about the owner's account, and the owner has already been texted.
    /// </summary>
    public static string ForUser(bool isOwner) =>
        isOwner
            ? $"I'm out of OpenAI credit, so I can't answer right now. Top up at {BillingUrl} and I'll pick straight back up."
            : "I can't answer right now — my owner has been notified. Try again a bit later.";
}
