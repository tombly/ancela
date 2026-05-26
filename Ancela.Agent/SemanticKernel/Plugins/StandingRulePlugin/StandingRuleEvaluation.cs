namespace Ancela.Agent.SemanticKernel.Plugins.StandingRulePlugin;

/// <summary>
/// Outcome of evaluating a standing rule.
/// <para>
/// <see cref="ShouldNotify"/> is the model's decision on whether the watched condition is
/// met and the user should be notified this cycle. The processor enforces the cooldown
/// window and, if allowed, sends <see cref="Message"/> to the owner's fixed number.
/// </para>
/// <see cref="Reasoning"/> is the model's explanation, recorded as the decision-audit trust signal.
/// </summary>
public record StandingRuleEvaluation
{
    public required bool ShouldNotify { get; init; }
    public string? Message { get; init; }
    public required string Reasoning { get; init; }
}
