using Ancela.Cli.Infrastructure;
using Spectre.Console;

namespace Ancela.Cli.Ui;

/// <summary>Renders the at-a-glance container overview shown on shell launch.</summary>
public sealed class Dashboard(CosmosBrowser _browser)
{
    public async Task RenderAsync(string endpoint, CancellationToken ct = default)
    {
        var counts = new Dictionary<string, long?>();

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots).SpinnerStyle(Theme.Accent)
            .StartAsync("Reading containers…", async ctx =>
            {
                foreach (var c in CosmosBrowser.Containers)
                {
                    ctx.Status($"Counting [deepskyblue1]{c.Name}[/]…");
                    try { counts[c.Name] = await _browser.CountAsync(endpoint, c.Name, ct); }
                    catch { counts[c.Name] = null; }
                }
            });

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey37).Expand();
        table.AddColumn("[bold]Container[/]");
        table.AddColumn(new TableColumn("[bold]Docs[/]").RightAligned());
        table.AddColumn("[bold]Description[/]");

        foreach (var c in CosmosBrowser.Containers)
        {
            var count = counts[c.Name];
            var countCell = count is null ? "[red]—[/]" : $"[springgreen2]{count:N0}[/]";
            table.AddRow($"[white]{c.Name}[/]", countCell, $"[grey]{c.Description}[/]");
        }

        AnsiConsole.Write(table);
    }
}
