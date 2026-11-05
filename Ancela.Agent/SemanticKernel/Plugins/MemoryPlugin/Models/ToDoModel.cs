namespace Ancela.Agent.SemanticKernel.Plugins.MemoryPlugin.Models;

public class ToDoModel
{
    public Guid Id { get; set; }
    public required string Content { get; set; }
    public required DateTimeOffset Created { get; set; }
    public required DateTimeOffset? Deleted { get; set; }

    /// <summary>The day it must be done by, when the user gave one. Most to-dos have none.</summary>
    public DateOnly? Due { get; set; }

    /// <summary>
    /// Not stored: days from the user's local today to <see cref="Due"/>, 0 on the day and negative
    /// once it has passed. get_todos sets it, so a morning briefing can tell what is close.
    /// </summary>
    public int? DaysUntilDue { get; set; }

    /// <summary>
    /// Who saved it, so a read of another user's to-do counts as outside text. Read from Cosmos
    /// (Newtonsoft) but left out of what the model is shown (System.Text.Json).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? UserPhoneNumber { get; set; }
}
