using System.Diagnostics;
using System.Text.Json;
using Ancela.Agent.Services;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Ancela.Agent;

/// <summary>
/// Automatically audits every Semantic Kernel plugin invocation.
/// Registered as IFunctionInvocationFilter so the kernel picks it up from DI.
/// </summary>
public class AuditFilter(IAuditLog _auditLog, CorrelationContext _correlation, ILogger<AuditFilter> _logger) : IFunctionInvocationFilter
{
    public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        _logger.LogDebug("AuditFilter: {Plugin}.{Function}", context.Function.PluginName, context.Function.Name);

        var start = Stopwatch.GetTimestamp();
        string? error = null;

        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            throw;
        }
        finally
        {
            var durationMs = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            context.Kernel.Data.TryGetValue("userPhoneNumber", out var userPhone);
            context.Kernel.Data.TryGetValue("agentPhoneNumber", out var agentPhone);

            // Skip if no user context — filter may fire outside of a chat turn (e.g. tests).
            if (userPhone is not string userPhoneNumber)
            {
                _logger.LogWarning("AuditFilter: no user context for {Plugin}.{Function} — skipping", context.Function.PluginName, context.Function.Name);
            }
            else
                await _auditLog.LogAsync(new AuditEntry
                {
                    UserPhoneNumber = userPhoneNumber,
                    AgentPhoneNumber = agentPhone as string ?? string.Empty,
                    Timestamp = DateTimeOffset.UtcNow,
                    CorrelationId = _correlation.Current,
                    Actor = "agent",
                    Category = "tool",
                    Plugin = context.Function.PluginName ?? string.Empty,
                    Function = context.Function.Name,
                    Arguments = SerializeArguments(context.Arguments),
                    Result = error is null ? Truncate(context.Result?.ToString()) : null,
                    Success = error is null,
                    Error = error,
                    DurationMs = durationMs,
                });
        }
    }

    private static string? SerializeArguments(KernelArguments? args)
    {
        if (args is null || args.Count == 0)
            return null;
        try
        {
            return JsonSerializer.Serialize(args.ToDictionary(k => k.Key, k => k.Value?.ToString()));
        }
        catch
        {
            return null;
        }
    }

    private static string? Truncate(string? value, int maxLength = 500)
    {
        if (value is null) return null;
        return value.Length > maxLength ? value[..maxLength] + "…" : value;
    }
}
