namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

public class EventModel
{
    public required string Description { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }

    /// <summary>An all-day event runs from midnight to midnight on the user's clock, the end being the day after.</summary>
    public bool IsAllDay { get; init; }
}
