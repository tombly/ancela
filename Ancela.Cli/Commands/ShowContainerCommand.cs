using System.ComponentModel;
using Ancela.Cli.Infrastructure;
using Ancela.Cli.Ui;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ancela.Cli.Commands;

/// <summary>Shows one document from any container as full, pretty-printed JSON.</summary>
public sealed class ShowContainerCommand(CosmosBrowser _browser) : AsyncCommand<ShowContainerCommand.Settings>
{
    public sealed class Settings : ConnectionSettings
    {
        [CommandArgument(0, "<CONTAINER>")]
        [Description("Container the document lives in.")]
        public string Container { get; init; } = string.Empty;

        [CommandArgument(1, "<ID>")]
        [Description("Document id — full GUID, or an 8-char (or longer) prefix.")]
        public string Id { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var endpoint = CosmosClientProvider.ResolveEndpoint(settings.Endpoint);
        if (endpoint is null)
            return Errors.NoEndpoint();
        if (!ContainerNames.Validate(settings.Container, out var container))
            return 1;

        // Exact id match when a full GUID is given, otherwise prefix match.
        var query = Guid.TryParse(settings.Id, out _)
            ? new QueryDefinition("SELECT * FROM c WHERE c.id = @id").WithParameter("@id", settings.Id)
            : new QueryDefinition("SELECT * FROM c WHERE STARTSWITH(c.id, @id)").WithParameter("@id", settings.Id);

        var docs = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots).SpinnerStyle(Theme.Accent)
            .StartAsync("Loading document…", _ => _browser.QueryAsync<JObject>(endpoint, container, query, cancellationToken));

        if (docs.Count == 0)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No document in[/] [deepskyblue1]{container}[/] [red]matching[/] [yellow]{settings.Id}[/].");
            return 1;
        }
        if (docs.Count > 1)
            AnsiConsole.MarkupLineInterpolated($"[yellow]{docs.Count} documents match that prefix; showing the first.[/]");

        var doc = docs[0];
        // Drop Cosmos system fields (_rid/_self/_etag/_attachments/_ts) — noise for a doc view.
        foreach (var sysField in doc.Properties().Where(p => p.Name.StartsWith('_')).ToList())
            sysField.Remove();

        AnsiConsole.Write(Theme.JsonPanel(doc.ToString(Formatting.Indented), $" {container} "));
        return 0;
    }
}
