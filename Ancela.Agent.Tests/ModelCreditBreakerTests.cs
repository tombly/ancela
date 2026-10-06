using System.Net;
using Ancela.Agent.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Ancela.Agent.Tests;

/// <summary>
/// Covers the out-of-credit breaker: telling an empty balance apart from an ordinary rate limit,
/// texting the owner once per outage rather than once per failure, short-circuiting calls while
/// the balance is known empty, and re-probing so recovery is noticed.
/// </summary>
public class ModelCreditBreakerTests
{
    private const string Owner = "+15551234567";

    public ModelCreditBreakerTests()
    {
        Environment.SetEnvironmentVariable("TWILIO_PHONE_NUMBER", "+10000000000");
        Environment.SetEnvironmentVariable("TWILIO_ACCOUNT_SID", "ACXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX");
        Environment.SetEnvironmentVariable("TWILIO_AUTH_TOKEN", "test-token");
        Environment.SetEnvironmentVariable("OWNER_PHONE_NUMBER", Owner);
    }

    // The exact shape seen in production on 2026-09-20: Semantic Kernel wrapping the provider's 429.
    private static Exception QuotaException() =>
        new HttpOperationException(
            HttpStatusCode.TooManyRequests,
            responseContent: null,
            message: "HTTP 429 (insufficient_quota: credit_balance_exhausted)",
            innerException: new InvalidOperationException("HTTP 429 (insufficient_quota: credit_balance_exhausted)"));

    // ---- Classification ----------------------------------------------------

    [Fact]
    public void Classifier_RecognizesTheProductionException() =>
        CreditAlertService.IsCreditExhausted(QuotaException()).Should().BeTrue();

    [Fact]
    public void Classifier_RecognizesMarkerInResponseContentOnly() =>
        CreditAlertService.IsCreditExhausted(new HttpOperationException(
            HttpStatusCode.TooManyRequests,
            responseContent: """{"error":{"code":"insufficient_quota"}}""",
            message: "HTTP 429",
            innerException: null)).Should().BeTrue();

    [Fact]
    public void Classifier_RecognizesMarkerNestedDeep() =>
        CreditAlertService.IsCreditExhausted(
            new InvalidOperationException("outer", new InvalidOperationException("middle", QuotaException())))
            .Should().BeTrue();

    [Fact]
    public void Classifier_RecognizesMarkerInsideAggregate() =>
        CreditAlertService.IsCreditExhausted(
            new AggregateException(new InvalidOperationException("unrelated"), QuotaException()))
            .Should().BeTrue();

    [Fact]
    public void Classifier_IgnoresOrdinaryRateLimit() =>
        // A plain 429 is transient and retry fixes it — tripping the breaker would take the agent
        // down over a hiccup.
        CreditAlertService.IsCreditExhausted(new HttpOperationException(
            HttpStatusCode.TooManyRequests,
            responseContent: """{"error":{"code":"rate_limit_exceeded"}}""",
            message: "HTTP 429 (rate_limit_exceeded)",
            innerException: null)).Should().BeFalse();

    [Theory]
    [InlineData("Graph token expired")]
    [InlineData("HTTP 500 (server_error)")]
    [InlineData("")]
    public void Classifier_IgnoresUnrelatedFailures(string message) =>
        CreditAlertService.IsCreditExhausted(new InvalidOperationException(message)).Should().BeFalse();

    [Fact]
    public void Classifier_IgnoresNull() => CreditAlertService.IsCreditExhausted(null).Should().BeFalse();

    // ---- One text per outage, regardless of how much autonomy is configured ----

    [Fact]
    public async Task ManyFailuresAcrossPaths_TextTheOwnerExactlyOnce()
    {
        var (service, sms, inner, clock) = Build(alwaysFails: true);

        // An hourly standing rule plus a daily task plus the owner texting in: 30 failures that
        // under a per-failure alert would be 30 texts.
        for (var i = 0; i < 30; i++)
        {
            await Invoke(service).Should().ThrowAsync<ModelOutOfFundsException>();
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        sms.SendCount.Should().Be(1, "the owner is told on the transition, not on every failure");
        sms.LastRecipient.Should().Be(Owner);
        sms.LastMessage.Should().Contain("out of OpenAI credit");
        inner.Calls.Should().Be(6,
            "30 minutes of failures at a 5-minute probe interval is 6 real calls, not 30 — the rest short-circuit");
    }

    [Fact]
    public async Task WhileOpen_CallsShortCircuit_WithoutReachingTheModel()
    {
        var (service, _, inner, _) = Build(alwaysFails: true);

        await Invoke(service).Should().ThrowAsync<ModelOutOfFundsException>();
        var afterTrip = inner.Calls;

        for (var i = 0; i < 5; i++)
            await Invoke(service).Should().ThrowAsync<ModelOutOfFundsException>();

        inner.Calls.Should().Be(afterTrip);
    }

    [Fact]
    public async Task AfterTheHalfOpenInterval_OneCallIsLetThrough()
    {
        var (service, _, inner, clock) = Build(alwaysFails: true);

        await Invoke(service).Should().ThrowAsync<ModelOutOfFundsException>();
        inner.Calls.Should().Be(1);

        clock.Advance(ModelAvailability.HalfOpenAfter + TimeSpan.FromSeconds(1));
        await Invoke(service).Should().ThrowAsync<ModelOutOfFundsException>();

        inner.Calls.Should().Be(2, "a short-circuited call can never discover that the balance was topped up");
    }

    [Fact]
    public async Task WhenCreditReturns_TheBreakerCloses_AndANewOutageNotifiesAgain()
    {
        var (service, sms, _, clock) = Build(alwaysFails: true);

        await Invoke(service).Should().ThrowAsync<ModelOutOfFundsException>();
        sms.SendCount.Should().Be(1);

        // Topped up: the next probe succeeds.
        clock.Advance(ModelAvailability.HalfOpenAfter + TimeSpan.FromSeconds(1));
        FakeChat.Current!.Fails = false;
        (await service.GetChatMessageContentsAsync(new ChatHistory("hi"))).Should().NotBeEmpty();

        // A later, separate outage must be reported.
        FakeChat.Current.Fails = true;
        await Invoke(service).Should().ThrowAsync<ModelOutOfFundsException>();

        sms.SendCount.Should().Be(2);
    }

    [Fact]
    public async Task WhenTheNotificationFails_TheNextFailureTriesAgain()
    {
        // Edge-triggered alerting has exactly one chance, so a Twilio hiccup at the moment of
        // transition must not swallow the only notification.
        var sms = new FlakySmsService { FailNextSend = true };
        var (service, _, _, _) = Build(alwaysFails: true, sms: sms);

        await Invoke(service).Should().ThrowAsync<ModelOutOfFundsException>();
        sms.SuccessfulSends.Should().Be(0);

        await Invoke(service).Should().ThrowAsync<ModelOutOfFundsException>();
        sms.SuccessfulSends.Should().Be(1);
    }

    [Fact]
    public async Task OrdinaryFailures_NeitherTripTheBreakerNorNotify()
    {
        var (service, sms, inner, _) = Build(alwaysFails: true, failWith: () => new HttpRequestException("socket closed"));

        await Invoke(service).Should().ThrowAsync<HttpRequestException>();
        await Invoke(service).Should().ThrowAsync<HttpRequestException>();

        sms.SendCount.Should().Be(0);
        inner.Calls.Should().Be(2, "a transient failure must stay retryable");
    }

    // ---- Reply copy ---------------------------------------------------------

    [Fact]
    public void OutOfFundsReply_TellsTheOwnerHow_ButLeaksNoBillingDetailToOthers()
    {
        OutOfFundsMessages.ForUser(isOwner: true).Should().Contain(OutOfFundsMessages.BillingUrl);

        var guest = OutOfFundsMessages.ForUser(isOwner: false);
        guest.Should().NotContain(OutOfFundsMessages.BillingUrl);
        guest.Should().NotContainEquivalentOf("credit").And.NotContainEquivalentOf("OpenAI");
    }

    // ---- Harness ------------------------------------------------------------

    private static Func<Task> Invoke(IChatCompletionService service) =>
        async () => await service.GetChatMessageContentsAsync(new ChatHistory("hi"));

    private static (IChatCompletionService, SpySmsService, FakeChat, FakeTimeProvider) Build(
        bool alwaysFails, SpySmsService? sms = null, Func<Exception>? failWith = null)
    {
        sms ??= new SpySmsService();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 20, 14, 30, 0, TimeSpan.Zero));
        var availability = new ModelAvailability(clock);
        var alerts = new CreditAlertService(availability, sms, new OwnerService(), NullLogger<CreditAlertService>.Instance);
        var inner = new FakeChat { Fails = alwaysFails, FailWith = failWith ?? QuotaException };
        FakeChat.Current = inner;

        return (new CreditAwareChatCompletionService(inner, availability, alerts), sms, inner, clock);
    }

    private sealed class FakeChat : IChatCompletionService
    {
        [ThreadStatic] public static FakeChat? Current;

        public int Calls { get; private set; }
        public bool Fails { get; set; }
        public Func<Exception> FailWith { get; set; } = () => new InvalidOperationException();

        public IReadOnlyDictionary<string, object?> Attributes => new Dictionary<string, object?>();

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Fails) throw FailWith();
            return Task.FromResult<IReadOnlyList<ChatMessageContent>>([new ChatMessageContent(AuthorRole.Assistant, "ok")]);
        }

        public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Fails) throw FailWith();
            yield return new StreamingChatMessageContent(AuthorRole.Assistant, "ok");
            await Task.CompletedTask;
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset _start) : TimeProvider
    {
        private DateTimeOffset _now = _start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private class SpySmsService() : SmsService()
    {
        public int SendCount { get; private set; }
        public string? LastRecipient { get; private set; }
        public string? LastMessage { get; private set; }

        public override Task Send(string phoneNumbers, string message)
        {
            SendCount++;
            LastRecipient = phoneNumbers;
            LastMessage = message;
            return Task.CompletedTask;
        }
    }

    private sealed class FlakySmsService : SpySmsService
    {
        public bool FailNextSend { get; set; }
        public int SuccessfulSends { get; private set; }

        public override Task Send(string phoneNumbers, string message)
        {
            if (FailNextSend)
            {
                FailNextSend = false;
                throw new HttpRequestException("Twilio unreachable");
            }
            SuccessfulSends++;
            return base.Send(phoneNumbers, message);
        }
    }
}
