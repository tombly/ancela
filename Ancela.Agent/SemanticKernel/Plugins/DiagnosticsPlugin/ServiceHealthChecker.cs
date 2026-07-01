using System.Diagnostics;
using Ancela.Agent.SemanticKernel.Plugins.DiagnosticsPlugin.Models;
using Ancela.Agent.SemanticKernel.Plugins.GoogleHealthPlugin;
using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using Ancela.Agent.SemanticKernel.Plugins.RemarkablePlugin;
using Ancela.Agent.SemanticKernel.Plugins.WebPlugin;
using Ancela.Agent.SemanticKernel.Plugins.YnabPlugin;
using Ancela.Agent.Services;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Cosmos;

namespace Ancela.Agent.SemanticKernel.Plugins.DiagnosticsPlugin;

public interface IServiceHealthChecker
{
    /// <summary>Probes every connected external service in parallel and reports per-service results.</summary>
    Task<ServiceProbeResult[]> CheckAllAsync();
}

/// <summary>
/// Reachability probes for the self-check. Each probe is the cheapest call that proves the
/// dependency is reachable AND its credential is still valid (an expired token is the most
/// common real failure, e.g. Google Health's ~weekly Testing-mode refresh tokens). The core
/// pipeline (Twilio inbound → Functions → Service Bus → Cosmos history → OpenAI) needs no
/// probe: a "how are you?" that arrives at all has already traversed it.
/// </summary>
public class ServiceHealthChecker(
    CosmosClient _cosmosClient,
    ServiceBusClient _serviceBusClient,
    IGraphClient _graphClient,
    YnabClient _ynabClient,
    ITavilyClient _tavilyClient,
    GoogleHealthClient _googleHealthClient,
    SmsService _smsService,
    IRemarkableService _remarkableService) : IServiceHealthChecker
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    private static readonly string[] QueueNames = ["chat-messages", "reminders", "standing-rules", "scheduled-tasks"];

    // Peek cap per dead-letter queue: enough to say "there's a problem and roughly how big".
    private const int DeadLetterPeekCap = 25;

    public async Task<ServiceProbeResult[]> CheckAllAsync()
    {
        var probes = new[]
        {
            ProbeAsync("cosmos-db", ProbeCosmosAsync),
            ProbeAsync("service-bus", ProbeServiceBusAsync),
            ProbeAsync("twilio", ProbeTwilioAsync),
            ProbeAsync("microsoft-graph", ProbeGraphAsync),
            ProbeAsync("ynab", ProbeYnabAsync),
            ProbeAsync("tavily-web-search", ProbeTavilyAsync),
            ProbeAsync("google-health", ProbeGoogleHealthAsync),
            ProbeAsync("remarkable", ProbeRemarkableAsync),
        };

        var results = await Task.WhenAll(probes);

        // No probe needed for the core path — producing this reply already exercised it.
        return
        [
            .. results,
            new ServiceProbeResult
            {
                Service = "core-pipeline (twilio-inbound, service-bus, openai)",
                Status = "ok",
                Detail = "implicitly verified — this conversation reached the model through it",
            },
        ];
    }

    private async Task<(string Status, string Detail)> ProbeCosmosAsync()
    {
        await _cosmosClient.GetContainer("anceladb", "users").ReadContainerAsync();
        return ("ok", "users container reachable");
    }

    private async Task<(string Status, string Detail)> ProbeServiceBusAsync()
    {
        // Peek each queue's dead-letter subqueue: proves connectivity with Listen rights only
        // (no Manage claim needed) and doubles as an anomaly signal — dead-lettered messages
        // mean a processor is failing repeatedly. The peeks run in parallel so they share the
        // probe's timeout budget instead of consuming it serially.
        var counts = await Task.WhenAll(QueueNames.Select(async queue =>
        {
            await using var receiver = _serviceBusClient.CreateReceiver(
                queue, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
            var messages = await receiver.PeekMessagesAsync(DeadLetterPeekCap);
            return (Queue: queue, messages.Count);
        }));

        var deadLettered = counts
            .Where(c => c.Count > 0)
            .Select(c => $"{c.Queue}: {c.Count}{(c.Count == DeadLetterPeekCap ? "+" : "")}")
            .ToList();

        return deadLettered.Count == 0
            ? ("ok", "all queues reachable; no dead-lettered messages")
            : ("degraded", $"dead-lettered messages found — {string.Join(", ", deadLettered)}");
    }

    private async Task<(string Status, string Detail)> ProbeTwilioAsync()
    {
        var status = await _smsService.CheckAccountStatusAsync();
        return string.Equals(status, "active", StringComparison.OrdinalIgnoreCase)
            ? ("ok", "account active")
            : ("degraded", $"account status is '{status}'");
    }

    private async Task<(string Status, string Detail)> ProbeGraphAsync()
    {
        await _graphClient.GetUserContactsAsync(maxResults: 1);
        return ("ok", "token valid; contacts reachable");
    }

    private async Task<(string Status, string Detail)> ProbeYnabAsync()
    {
        Exception? lastEx = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await _ynabClient.CheckUserAsync();
                return ("ok", "token valid; /user reachable");
            }
            catch (Exception ex)
            {
                lastEx = ex;
            }
        }
        throw lastEx!;
    }

    private async Task<(string Status, string Detail)> ProbeTavilyAsync()
    {
        await _tavilyClient.SearchAsync("ancela self-check ping", maxResults: 1);
        return ("ok", "search reachable (spent 1 search credit)");
    }

    private async Task<(string Status, string Detail)> ProbeGoogleHealthAsync()
    {
        // Exercises the full token lifecycle (refresh if stale) plus one cheap data read, so an
        // expired Testing-mode refresh token surfaces here with its re-auth instructions.
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        await _googleHealthClient.GetHeartRateAsync(today, today);
        return ("ok", "token valid; API reachable");
    }

    private async Task<(string Status, string Detail)> ProbeRemarkableAsync()
    {
        await _remarkableService.VerifyAsync();
        return ("ok", "device token valid; session minted");
    }

    /// <summary>
    /// Runs one probe with a timeout, turning exceptions and hangs into "fail" results so a
    /// single dead dependency never sinks the whole self-check. A timed-out probe task is
    /// abandoned, not awaited — its eventual fault is observed nowhere and that's intentional.
    /// </summary>
    private static async Task<ServiceProbeResult> ProbeAsync(string service, Func<Task<(string Status, string Detail)>> probe)
    {
        var start = Stopwatch.GetTimestamp();
        long Elapsed() => (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        try
        {
            var task = probe();
            var completed = await Task.WhenAny(task, Task.Delay(ProbeTimeout));
            if (completed != task)
                return new ServiceProbeResult
                {
                    Service = service,
                    Status = "fail",
                    LatencyMs = Elapsed(),
                    Detail = $"timed out after {ProbeTimeout.TotalSeconds:0}s",
                };

            var (status, detail) = await task;
            return new ServiceProbeResult { Service = service, Status = status, LatencyMs = Elapsed(), Detail = detail };
        }
        catch (Exception ex)
        {
            return new ServiceProbeResult { Service = service, Status = "fail", LatencyMs = Elapsed(), Detail = ex.Message };
        }
    }
}
