using Ancela.Agent.SemanticKernel;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

// Needed for IFunctionInvocationFilter
#pragma warning disable SKEXP0001

namespace Ancela.Agent;

/// <summary>
/// Defense-in-depth guard that hard-denies invocations the model should never have been able
/// to make — a default-deny backstop, so even if a non-advertised function enters the model's
/// call list it is blocked before execution. Enforces two orthogonal restrictions that mirror
/// what <see cref="Agent"/> advertises:
/// <list type="bullet">
/// <item>Profile allow-lists for autonomous profiles (StandingRule, ScheduledTask).</item>
/// <item>Owner-only functions (sending SMS/email, writing the owner's calendar) for non-owners.</item>
/// </list>
/// </summary>
public class AutonomousToolGuardFilter(ILogger<AutonomousToolGuardFilter> _logger) : IFunctionInvocationFilter
{
    public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        // Default-deny the profile too: a missing or non-KernelProfile value falls back to the
        // most restrictive profile, not Chat (which is unrestricted). KernelFactory always sets
        // this, so the fallback is unreachable today — but a backstop whose own fallback opens
        // the gate is not a backstop. Denials still flow through the normal path below, so the
        // audit markers AuditAnomalyScanner matches are unchanged.
        var profile = context.Kernel.Data.TryGetValue("profile", out var p) && p is KernelProfile kp
            ? kp
            : KernelProfile.StandingRule;

        if (!KernelProfilePolicy.IsAllowed(profile, context.Function.Name))
        {
            _logger.LogWarning(
                "AutonomousToolGuardFilter: blocked {Function} in {Profile} profile — possible prompt injection.",
                context.Function.Name, profile);

            throw new InvalidOperationException(
                $"Function '{context.Function.Name}' is not permitted in the {profile} profile.");
        }

        // Default-deny owner-only functions: missing/false isOwner is treated as not-owner.
        var isOwner = context.Kernel.Data.TryGetValue("isOwner", out var o) && o is true;
        if (!isOwner && KernelProfilePolicy.IsOwnerOnly(context.Function.Name))
        {
            _logger.LogWarning(
                "AutonomousToolGuardFilter: blocked owner-only {Function} for non-owner caller.",
                context.Function.Name);

            throw new InvalidOperationException(
                $"Function '{context.Function.Name}' may only be invoked by the owner.");
        }

        await next(context);
    }
}
