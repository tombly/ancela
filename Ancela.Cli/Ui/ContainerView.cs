using System.Globalization;
using Newtonsoft.Json.Linq;
using Spectre.Console;

namespace Ancela.Cli.Ui;

/// <summary>
/// Renders documents from any container generically (as JObjects), with a curated column
/// set per container and light decoding of the enum/int fields the agent stores.
/// </summary>
public static class ContainerView
{
    /// <summary>Preferred columns per container (camelCase field names). Fallback is all scalar fields.</summary>
    private static readonly Dictionary<string, string[]> Columns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["audit"] = ["id", "timestamp", "actor", "category", "plugin", "function", "userPhoneNumber", "success", "durationMs"],
        ["users"] = ["id", "userPhoneNumber", "name", "timeZone", "createdAt", "registeredAt"],
        ["history"] = ["id", "userPhoneNumber", "messageType", "content", "timestamp"],
        ["todos"] = ["id", "userPhoneNumber", "content", "created", "deleted"],
        ["knowledge"] = ["id", "userPhoneNumber", "content", "created", "deleted"],
        ["reminders"] = ["id", "userPhoneNumber", "dueAt", "message", "status", "sentAt"],
        ["standing_rules"] = ["id", "userPhoneNumber", "description", "evaluationIntervalHours", "status", "lastEvaluatedAt", "lastNotifiedAt"],
        ["scheduled_tasks"] = ["id", "userPhoneNumber", "description", "timeOfDay", "daysOfWeek", "status", "lastRunAt"],
        ["projects"] = ["id", "userPhoneNumber", "name", "isArchived", "updatedAt"],
    };

    // Integer enum fields → value names, keyed by "container.field".
    private static readonly Dictionary<string, string[]> Enums = new(StringComparer.OrdinalIgnoreCase)
    {
        ["history.messageType"] = ["User", "Agent"],
        ["reminders.status"] = ["Scheduled", "Sent", "Canceled"],
        ["standing_rules.status"] = ["Active", "Paused", "Done"],
        ["scheduled_tasks.status"] = ["Active", "Paused"],
    };

    private static readonly HashSet<string> SystemFields =
        new(StringComparer.OrdinalIgnoreCase) { "_rid", "_self", "_etag", "_attachments", "_ts", "agentPhoneNumber" };

    public static Table BuildListTable(string container, IReadOnlyList<JObject> docs, bool showAll)
    {
        var fields = ResolveColumns(container, docs, showAll);

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey37).Expand();
        foreach (var f in fields)
            table.AddColumn($"[bold]{Markup.Escape(f)}[/]");

        foreach (var doc in docs)
        {
            var cells = fields.Select(f => new Markup(FormatCell(container, f, doc[FindKey(doc, f)]))).ToArray();
            table.AddRow(cells);
        }
        return table;
    }

    private static string[] ResolveColumns(string container, IReadOnlyList<JObject> docs, bool showAll)
    {
        if (!showAll && Columns.TryGetValue(container, out var preferred))
            return preferred;

        // Auto: every non-system top-level field seen, id first.
        var seen = new List<string>();
        foreach (var doc in docs)
            foreach (var prop in doc.Properties())
                if (!SystemFields.Contains(prop.Name) && !seen.Contains(prop.Name))
                    seen.Add(prop.Name);

        return [.. seen.OrderBy(n => n.Equals("id", StringComparison.OrdinalIgnoreCase) ? 0 : 1)];
    }

    /// <summary>Case-insensitive property lookup so curated camelCase names still match odd casing.</summary>
    private static string? FindKey(JObject doc, string field) =>
        doc.Properties().FirstOrDefault(p => p.Name.Equals(field, StringComparison.OrdinalIgnoreCase))?.Name;

    private static string FormatCell(string container, string field, JToken? value)
    {
        if (value is null || value.Type == JTokenType.Null)
            return "[grey39]—[/]";

        // id → short form.
        if (field.Equals("id", StringComparison.OrdinalIgnoreCase))
            return $"[grey39]{Markup.Escape(Short(value.ToString()))}[/]";

        // Known enum int → name.
        if (Enums.TryGetValue($"{container}.{field}", out var names)
            && value.Type == JTokenType.Integer)
        {
            var i = value.Value<int>();
            return i >= 0 && i < names.Length ? names[i] : i.ToString();
        }

        var text = value.Type == JTokenType.Array
            ? string.Join(",", value.Select(t => t.ToString()))
            : value.ToString();

        // ISO date-ish strings → compact local-ish display.
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
            return $"[grey]{dto:yyyy-MM-dd HH:mm}[/]";

        return Markup.Escape(Truncate(text, 60));
    }

    private static string Short(string id) => id.Length >= 8 ? id[..8] : id;

    private static string Truncate(string s, int max) =>
        s.Length > max ? s[..max] + "…" : s;
}
