namespace Ancela.Agent.SemanticKernel.Plugins.StandingRulePlugin;

/// <summary>
/// Outcome of evaluating a standing rule. <see cref="Notified"/> reflects whether the
/// agent chose to send the user an SMS this cycle; <see cref="Reasoning"/> is the agent's
/// explanation, recorded as the decision-audit trust signal.
/// </summary>
public record StandingRuleEvaluation
{
    public required bool Notified { get; init; }
    public required string Reasoning { get; init; }
}
