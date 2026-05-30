using Ancela.Cli.Infrastructure;
using Spectre.Console;

namespace Ancela.Cli.Commands;

/// <summary>Validates a user-supplied container name against the known catalog.</summary>
public static class ContainerNames
{
    /// <summary>
    /// Resolves <paramref name="input"/> to the canonical container name (case-insensitive).
    /// Prints a helpful error and returns false if unknown.
    /// </summary>
    public static bool Validate(string input, out string canonical)
    {
        var match = CosmosBrowser.Containers
            .FirstOrDefault(c => c.Name.Equals(input, StringComparison.OrdinalIgnoreCase));

        if (match.Name is not null)
        {
            canonical = match.Name;
            return true;
        }

        canonical = string.Empty;
        var known = string.Join(", ", CosmosBrowser.Containers.Select(c => c.Name));
        AnsiConsole.MarkupLineInterpolated($"[red]Unknown container[/] [yellow]{input}[/]. Known: [grey]{known}[/].");
        return false;
    }
}
