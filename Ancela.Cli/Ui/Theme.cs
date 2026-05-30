using Spectre.Console;
using Spectre.Console.Json;
using Spectre.Console.Rendering;

namespace Ancela.Cli.Ui;

/// <summary>Shared visual vocabulary so every view feels like one tool.</summary>
public static class Theme
{
    /// <summary>Spinner/accent style used across commands.</summary>
    public static readonly Spectre.Console.Style Accent = new(Color.DeepSkyBlue1);

    public static string CategoryColor(string? category) => category switch
    {
        "tool" => "deepskyblue1",
        "web" => "mediumpurple1",
        "sms-send" => "springgreen2",
        "rule-decision" => "gold1",
        "session" => "grey70",
        _ => "white",
    };

    public static string ActorColor(string? actor) => actor switch
    {
        "user" => "aqua",
        "agent" => "mediumpurple1",
        "system" => "grey70",
        _ => "white",
    };

    public static string SuccessGlyph(bool success) =>
        success ? "[green]✓[/]" : "[red]✗[/]";

    /// <summary>Compact human relative time, e.g. "3m", "5h", "2d".</summary>
    public static string Relative(DateTimeOffset when)
    {
        var d = DateTimeOffset.UtcNow - when;
        if (d < TimeSpan.Zero) return "now";
        if (d.TotalMinutes < 1) return $"{(int)d.TotalSeconds}s";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}h";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays}d";
        return $"{(int)(d.TotalDays / 7)}w";
    }

    /// <summary>
    /// Renders a string as a syntax-highlighted JSON panel when it parses as JSON,
    /// otherwise as a raw-text panel. Audit Result fields are truncated when large,
    /// so invalid JSON is expected and must not crash rendering.
    /// </summary>
    public static IRenderable JsonPanel(string? json, string header)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Panel("[grey](none)[/]")
            {
                Header = new PanelHeader(header),
                Border = BoxBorder.Rounded,
                BorderStyle = new Spectre.Console.Style(Color.Grey37),
            };

        // JsonText parses lazily at render time, so validate eagerly here.
        IRenderable body;
        if (IsValidJson(json))
        {
            body = new JsonText(json);
        }
        else
        {
            header += " [grey](raw)[/]";
            body = new Markup(Markup.Escape(json));
        }

        return new Panel(body)
        {
            Header = new PanelHeader(header),
            Border = BoxBorder.Rounded,
            BorderStyle = new Spectre.Console.Style(Color.Grey37),
        };
    }

    private static bool IsValidJson(string text)
    {
        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(text);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
