using Ancela.Cli.Infrastructure;
using Ancela.Cli.Ui;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ancela.Cli.Shell;

/// <summary>
/// The interactive mode entered when `ancela` is launched with no arguments:
/// a banner + dashboard, then a menu-driven REPL that dispatches the same commands used headless.
/// </summary>
public sealed class InteractiveShell(CommandApp _app, IServiceProvider _services)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        RenderBanner();

        var endpoint = CosmosClientProvider.ResolveEndpoint(null);
        if (endpoint is null)
        {
            AnsiConsole.MarkupLine("[yellow]No Cosmos endpoint resolved.[/] Set [yellow]ANCELA_COSMOS_ENDPOINT[/] " +
                "or pass [yellow]--endpoint[/] on commands. Starting shell anyway.");
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated($"[grey]Connected to[/] [deepskyblue1]{endpoint}[/]\n");
            await _services.GetRequiredService<Dashboard>().RenderAsync(endpoint, ct);
        }

        return await Loop(ct);
    }

    private async Task<int> Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var verb = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title("\n[grey]Command:[/]")
                .HighlightStyle(Theme.Accent)
                .AddChoices("list", "show", "ping", "clear", "exit"));

            switch (verb)
            {
                case "exit":
                    return 0;
                case "clear":
                    AnsiConsole.Clear();
                    RenderBanner();
                    continue;
                case "ping":
                    await Dispatch(["ping"]);
                    continue;
            }

            var container = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title($"[deepskyblue1]{verb}[/] which container?")
                .HighlightStyle(Theme.Accent)
                .AddChoices(CosmosBrowser.Containers.Select(c => c.Name)));

            string[] args;
            if (verb == "show")
            {
                var id = AnsiConsole.Prompt(
                    new TextPrompt<string>("[grey]document id (full or 8-char prefix):[/]"));
                args = [verb, container, id];
            }
            else
            {
                var user = AnsiConsole.Prompt(
                    new TextPrompt<string>("[grey]filter by user phone (enter to skip):[/]").AllowEmpty());
                args = string.IsNullOrWhiteSpace(user)
                    ? [verb, container]
                    : [verb, container, "--user", user];
            }

            await Dispatch(args);
            AnsiConsole.WriteLine();
        }

        return 0;
    }

    private async Task Dispatch(string[] args)
    {
        try
        {
            await _app.RunAsync(args);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.GetType().Name}:[/] {ex.Message}");
        }
    }

    private static void RenderBanner()
    {
        var left = new Markup(
            "[bold white]Ancela[/]\n" +
            "[grey]cosmos data console[/]\n\n" +
            "[deepskyblue1]    ▄████▄    [/]\n" +
            "[deepskyblue1]   █ ◉  ◉ █   [/]\n" +
            "[deepskyblue1]    ▀████▀    [/]\n\n" +
            "[grey39]browse · query · inspect[/]");

        var right = new Markup(
            "[bold deepskyblue1]Quick start[/]\n" +
            "  [white]list[/] [grey]<container>[/]        browse documents newest-first\n" +
            "  [white]show[/] [grey]<container> <id>[/]   show full document as JSON\n" +
            "  [white]ping[/]                    count all containers\n\n" +
            "[bold deepskyblue1]Containers[/]\n" +
            "  [grey]audit  users  history  todos[/]\n" +
            "  [grey]knowledge  reminders  standing_rules  scheduled_tasks[/]");

        var grid = new Grid()
            .AddColumn(new GridColumn().PadRight(4).Width(26))
            .AddColumn();
        grid.AddRow(left, right);

        AnsiConsole.Write(new Panel(grid)
        {
            Header = new PanelHeader("[deepskyblue1] Ancela [/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.DeepSkyBlue1),
            Padding = new Padding(2, 1),
        });
        AnsiConsole.WriteLine();
    }
}
