using System.ComponentModel;
using Ancela.Cli.Infrastructure;
using Ancela.Cli.Ui;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ancela.Cli.Commands;

/// <summary>Generic newest-first listing of any container's documents.</summary>
public sealed class ListContainerCommand(CosmosBrowser _browser) : AsyncCommand<ListContainerCommand.Settings>
{
    public sealed class Settings : ConnectionSettings
    {
        [CommandArgument(0, "<CONTAINER>")]
        [Description("Container to list: audit, users, history, todos, knowledge, reminders, standing_rules, scheduled_tasks.")]
        public string Container { get; init; } = string.Empty;

        [CommandOption("-u|--user <PHONE>")]
        [Description("Filter by user phone number.")]
        public string? User { get; init; }

        [CommandOption("-n|--limit <N>")]
        [Description("Max rows to return (default 50).")]
        public int Limit { get; init; } = 50;

        [CommandOption("-a|--all")]
        [Description("Show every field instead of the curated column set.")]
        public bool All { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var endpoint = CosmosClientProvider.ResolveEndpoint(settings.Endpoint);
        if (endpoint is null)
            return Errors.NoEndpoint();
        if (!ContainerNames.Validate(settings.Container, out var container))
            return 1;

        var where = string.IsNullOrWhiteSpace(settings.User) ? string.Empty : "WHERE c.userPhoneNumber = @user";
        var top = settings.Limit > 0 ? $"TOP {settings.Limit} " : string.Empty;
        var query = new QueryDefinition($"SELECT {top}* FROM c {where} ORDER BY c._ts DESC");
        if (!string.IsNullOrWhiteSpace(settings.User))
            query = query.WithParameter("@user", settings.User);

        var docs = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots).SpinnerStyle(Theme.Accent)
            .StartAsync($"Querying [deepskyblue1]{container}[/]…", _ => _browser.QueryAsync<JObject>(endpoint, container, query, cancellationToken));

        if (docs.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No matching documents.[/]");
            return 0;
        }

        AnsiConsole.Write(ContainerView.BuildListTable(container, docs, settings.All));
        AnsiConsole.MarkupLineInterpolated($"[grey]{docs.Count} docs — `show {container} <id>` for the full document.[/]");
        return 0;
    }
}
