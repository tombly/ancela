using Ancela.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ancela.Cli.Commands;

/// <summary>Proves connectivity by listing every container with its live document count.</summary>
public sealed class PingCommand(CosmosBrowser _browser) : AsyncCommand<ConnectionSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ConnectionSettings settings, CancellationToken cancellationToken)
    {
        var endpoint = CosmosClientProvider.ResolveEndpoint(settings.Endpoint);
        if (endpoint is null)
        {
            AnsiConsole.MarkupLine("[red]No Cosmos endpoint.[/] Pass [yellow]--endpoint[/], or set " +
                "[yellow]ANCELA_COSMOS_ENDPOINT[/] / [yellow]ANCELA_RESOURCE_PREFIX[/].");
            return 1;
        }

        AnsiConsole.MarkupLineInterpolated($"[grey]Connecting to[/] [deepskyblue1]{endpoint}[/]");

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey37);
        table.AddColumn("[bold]Container[/]");
        table.AddColumn("[bold]Description[/]");
        table.AddColumn(new TableColumn("[bold]Docs[/]").RightAligned());

        try
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("cyan"))
                .StartAsync("Querying containers…", async ctx =>
                {
                    foreach (var c in CosmosBrowser.Containers)
                    {
                        ctx.Status($"Counting [deepskyblue1]{c.Name}[/]…");
                        long count;
                        try
                        {
                            count = await _browser.CountAsync(endpoint, c.Name, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            table.AddRow($"[white]{c.Name}[/]", c.Description, $"[red]err: {Markup.Escape(ex.Message.Split('\n')[0])}[/]");
                            continue;
                        }
                        table.AddRow($"[white]{c.Name}[/]", $"[grey]{c.Description}[/]", $"[green]{count:N0}[/]");
                    }
                });
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Connection failed:[/] {ex.Message}");
            AnsiConsole.MarkupLine("[grey]Is your [yellow]az login[/] current and does it have the Cosmos Data Reader/Contributor role?[/]");
            return 1;
        }

        AnsiConsole.Write(table);
        return 0;
    }
}
