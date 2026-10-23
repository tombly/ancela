using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ancela.Agent;

namespace Ancela.Cli.Chat;

/// <summary>
/// One-line summaries for the chat's tool lines, from what <see cref="AuditFilter"/> recorded:
/// arguments as a JSON object of strings, results as JSON or text (or a truncation envelope).
/// </summary>
public static class ChatFormat
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary><c>{"query":"mill creek","maxResults":"5"}</c> → <c>query: "mill creek", maxResults: 5</c>.</summary>
    public static string Arguments(string? json, int max = 70)
    {
        if (string.IsNullOrWhiteSpace(json))
            return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Line(json, max);

            var parts = doc.RootElement.EnumerateObject()
                .Where(p => p.Value.ValueKind != JsonValueKind.Null)
                .Select(p =>
                {
                    var value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? string.Empty : p.Value.GetRawText();
                    var plain = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                        || value is "True" or "False" or "true" or "false";
                    return $"{p.Name}: {(plain ? value : $"\"{Line(value, 30)}\"")}";
                });
            return Line(string.Join(", ", parts), max);
        }
        catch (JsonException)
        {
            return Line(json, max);
        }
    }

    /// <summary>What a tool returned, in a few words: "3 results", "Nothing found", or its text's first line.</summary>
    public static string Result(string? result, int max = 90)
    {
        if (string.IsNullOrWhiteSpace(result))
            return "Done";
        if (result.Contains(EmptyResultFilter.NothingFoundMarker, StringComparison.Ordinal))
            return "Nothing found";
        try
        {
            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;
            return root.ValueKind switch
            {
                JsonValueKind.Array => Count(root.GetArrayLength()),
                // AuditFilter's envelope for a result over its size limit.
                JsonValueKind.Object when root.TryGetProperty("truncated", out _) => "Large result",
                JsonValueKind.Object => "Done",
                JsonValueKind.String => Line(root.GetString() ?? string.Empty, max),
                _ => Line(root.GetRawText(), max),
            };
        }
        catch (JsonException)
        {
            return Line(result, max);
        }
    }

    private static string Count(int n) => n switch
    {
        0 => "Nothing found",
        1 => "1 result",
        _ => $"{n} results",
    };

    /// <summary>Collapses whitespace to one line and cuts it at <paramref name="max"/> with an ellipsis.</summary>
    public static string Line(string text, int max)
    {
        var line = Whitespace.Replace(text, " ").Trim();
        return line.Length <= max ? line : line[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>"07:30" today, "Mon 07:30" this week, "Oct 21 07:30" before that, in your time zone.</summary>
    public static string When(DateTimeOffset when, TimeZoneInfo timeZone, DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(when, timeZone);
        var today = TimeZoneInfo.ConvertTime(now, timeZone).Date;
        var format = local.Date == today ? "HH:mm"
            : local.Date > today.AddDays(-7) ? "ddd HH:mm"
            : "MMM d HH:mm";
        return local.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>How the chat labels what Ancela sent (an <see cref="Agent.Services.OutboundLog"/> kind).</summary>
    public static string Kind(string kind) => kind switch
    {
        "reminder" => "Reminder",
        "scheduled-task" => "Scheduled task",
        "standing-rule" => "Rule alert",
        "reply" => "Reply",
        _ => kind,
    };
}
