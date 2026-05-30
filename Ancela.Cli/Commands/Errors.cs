using Spectre.Console;

namespace Ancela.Cli.Commands;

/// <summary>Consistent error output for common failure modes.</summary>
public static class Errors
{
    /// <summary>Prints the missing-endpoint message and returns the exit code to bubble up.</summary>
    public static int NoEndpoint()
    {
        AnsiConsole.MarkupLine("[red]No Cosmos endpoint.[/] Pass [yellow]--endpoint[/], or set " +
            "[yellow]ANCELA_COSMOS_ENDPOINT[/] / [yellow]ANCELA_RESOURCE_PREFIX[/].");
        return 1;
    }
}
