using Ancela.Agent.SemanticKernel;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

// Needed for IFunctionInvocationFilter
#pragma warning disable SKEXP0001

namespace Ancela.Agent;

/// <summary>
/// Defense-in-depth guard for autonomous kernel profiles (StandingRule, ScheduledTask).
/// Hard-denies any invocation that <see cref="KernelProfilePolicy"/> doesn't allow for the
/// profile — a default-deny backstop, so even if a non-advertised function somehow enters
/// the model's call list it is blocked before execution. Chat/Onboarding are unrestricted.
/// </summary>
public class AutonomousToolGuardFilter(ILogger<AutonomousToolGuardFilter> _logger) : IFunctionInvocationFilter
{
    public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        var profile = context.Kernel.Data.TryGetValue("profile", out var p) ? (KernelProfile)p : KernelProfile.Chat;

        if (!KernelProfilePolicy.IsAllowed(profile, context.Function.Name))
        {
            _logger.LogWarning(
                "AutonomousToolGuardFilter: blocked {Function} in {Profile} profile — possible prompt injection.",
                context.Function.Name, profile);

            throw new InvalidOperationException(
                $"Function '{context.Function.Name}' is not permitted in the {profile} profile.");
        }

        await next(context);
    }
}
