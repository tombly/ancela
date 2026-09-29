using System.Net;
using Ancela.Agent.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.Tests;

/// <summary>
/// Covers the OpenAI credit-exhaustion backstop: the classifier that separates an empty balance
/// from an ordinary rate limit, and the throttle that turns a failure repeating on every queue
/// message into one text a day.
/// </summary>
public class QuotaAlertTests
{
    private const string Agent = "+10000000000";
    private const string Owner = "+15551234567";

    public QuotaAlertTests()
    {
        Environment.SetEnvironmentVariable("TWILIO_PHONE_NUMBER", Agent);
        Environment.SetEnvironmentVariable("TWILIO_ACCOUNT_SID", "ACXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX");
        Environment.SetEnvironmentVariable("TWILIO_AUTH_TOKEN", "test-token");
        Environment.SetEnvironmentVariable("OWNER_PHONE_NUMBER", Owner);
    }

    // The exact shape seen in production on 2026-09-20: Semantic Kernel wrapping the provider's
    // 429, with the marker only in the message.
    private static Exception ProductionShapedQuotaException() =>
        new HttpOperationException(
            HttpStatusCode.TooManyRequests,
            responseContent: null,
            message: "HTTP 429 (insufficient_quota: credit_balance_exhausted)",
            innerException: new InvalidOperationException("HTTP 429 (insufficient_quota: credit_balance_exhausted)"));

    [Fact]
    public void Classifier_RecognizesProductionQuotaException()
    {
        QuotaAlertService.IsCreditExhausted(ProductionShapedQuotaException()).Should().BeTrue();
    }

    [Fact]
    public void Classifier_RecognizesMarkerInResponseContentOnly()
    {
        var exception = new HttpOperationException(
            HttpStatusCode.TooManyRequests,
            responseContent: """{"error":{"code":"insufficient_quota"}}""",
            message: "HTTP 429",
            innerException: null);

        QuotaAlertService.IsCreditExhausted(exception).Should().BeTrue();
    }

    [Fact]
    public void Classifier_RecognizesMarkerNestedDeep()
    {
        var exception = new InvalidOperationException("outer",
            new InvalidOperationException("middle", ProductionShapedQuotaException()));

        QuotaAlertService.IsCreditExhausted(exception).Should().BeTrue();
    }

    [Fact]
    public void Classifier_RecognizesMarkerInsideAggregate()
    {
        var exception = new AggregateException(
            new InvalidOperationException("unrelated"),
            ProductionShapedQuotaException());

        QuotaAlertService.IsCreditExhausted(exception).Should().BeTrue();
    }

    [Fact]
    public void Classifier_IgnoresOrdinaryRateLimit()
    {
        // A plain 429 is transient and retry fixes it — alerting on it would cry wolf.
        var exception = new HttpOperationException(
            HttpStatusCode.TooManyRequests,
            responseContent: """{"error":{"code":"rate_limit_exceeded"}}""",
            message: "HTTP 429 (rate_limit_exceeded)",
            innerException: null);

        QuotaAlertService.IsCreditExhausted(exception).Should().BeFalse();
    }

    [Theory]
    [InlineData("Graph token expired")]
    [InlineData("HTTP 500 (server_error)")]
    [InlineData("")]
    public void Classifier_IgnoresUnrelatedFailures(string message)
    {
        QuotaAlertService.IsCreditExhausted(new InvalidOperationException(message)).Should().BeFalse();
    }

    [Fact]
    public void Classifier_IgnoresNull()
    {
        QuotaAlertService.IsCreditExhausted(null).Should().BeFalse();
    }

    [Fact]
    public async Task RepeatedFailures_SendExactlyOneTextPerDay()
    {
        var (service, sms, clock) = Build();

        // Five failures a day for nine days: the September 2026 outage, unthrottled that is 45 texts.
        for (var day = 0; day < 9; day++)
        {
            for (var fire = 0; fire < 5; fire++)
            {
                await service.NotifyIfCreditExhaustedAsync(ProductionShapedQuotaException(), Agent, "test");
                clock.Advance(TimeSpan.FromHours(1));
            }

            clock.Advance(TimeSpan.FromHours(19));
        }

        sms.SendCount.Should().Be(9, "the throttle allows one alert per 24h window while the balance stays empty");
        sms.LastRecipient.Should().Be(Owner);
        sms.LastMessage.Should().Contain("out of OpenAI credit");
    }

    [Fact]
    public async Task BurstOfFailures_SendsOnlyOneText()
    {
        var (service, sms, _) = Build();

        for (var i = 0; i < 20; i++)
            await service.NotifyIfCreditExhaustedAsync(ProductionShapedQuotaException(), Agent, "test");

        sms.SendCount.Should().Be(1);
    }

    [Fact]
    public async Task UnrelatedFailure_SendsNothing()
    {
        var (service, sms, _) = Build();

        await service.NotifyIfCreditExhaustedAsync(new InvalidOperationException("Graph token expired"), Agent, "test");

        sms.SendCount.Should().Be(0);
    }

    [Fact]
    public async Task AlertFailure_IsSwallowed()
    {
        // Alerting must never mask the failure it reports, or a quota outage becomes a crash loop.
        var sms = new ThrowingSmsService();
        var service = new QuotaAlertService(
            new InMemoryQuotaAlertStore(), sms, new OwnerService(),
            new FakeTimeProvider(DateTimeOffset.UnixEpoch), NullLogger<QuotaAlertService>.Instance);

        var act = async () => await service.NotifyIfCreditExhaustedAsync(ProductionShapedQuotaException(), Agent, "test");

        await act.Should().NotThrowAsync();
    }

    private static (QuotaAlertService, SpySmsService, FakeTimeProvider) Build()
    {
        var sms = new SpySmsService();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 20, 14, 30, 0, TimeSpan.Zero));
        var service = new QuotaAlertService(
            new InMemoryQuotaAlertStore(), sms, new OwnerService(), clock, NullLogger<QuotaAlertService>.Instance);
        return (service, sms, clock);
    }

    /// <summary>Mirrors the Cosmos store's claim semantics without Cosmos.</summary>
    private sealed class InMemoryQuotaAlertStore : IQuotaAlertStore
    {
        private readonly Dictionary<string, DateTimeOffset> _lastNotified = [];

        public Task<bool> TryClaimAsync(string agentPhoneNumber, DateTimeOffset now, TimeSpan window)
        {
            if (_lastNotified.TryGetValue(agentPhoneNumber, out var last) && now - last < window)
                return Task.FromResult(false);

            _lastNotified[agentPhoneNumber] = now;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset _start) : TimeProvider
    {
        private DateTimeOffset _now = _start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class SpySmsService() : SmsService()
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

    private sealed class ThrowingSmsService() : SmsService()
    {
        public override Task Send(string phoneNumbers, string message) =>
            throw new HttpRequestException("Twilio unreachable");
    }
}
