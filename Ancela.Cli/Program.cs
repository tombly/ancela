using Ancela.Cli.Commands;
using Ancela.Cli.Infrastructure;
using Ancela.Cli.Shell;
using Ancela.Cli.Ui;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

var services = new ServiceCollection();
services.AddSingleton<CosmosClientProvider>();
services.AddSingleton<CosmosBrowser>();
services.AddSingleton<Dashboard>();

// Register command types explicitly. Spectre would register them during RunAsync, but the
// shared provider is built once (for the shell) before that happens, so it must already
// contain them.
services.AddTransient<PingCommand>();
services.AddTransient<ListContainerCommand>();
services.AddTransient<ShowContainerCommand>();
services.AddTransient<EnrollCommand>();

var registrar = new TypeRegistrar(services);
var app = new CommandApp(registrar);
app.Configure(config =>
{
    config.SetApplicationName("ancela");

    config.AddCommand<PingCommand>("ping")
        .WithDescription("List every Cosmos container with its live document count.");

    config.AddCommand<ListContainerCommand>("list")
        .WithDescription("List documents in any container (newest first).")
        .WithExample("list", "reminders")
        .WithExample("list", "users", "--user", "+15551234567");

    config.AddCommand<ShowContainerCommand>("show")
        .WithDescription("Show one document from any container as full JSON.")
        .WithExample("show", "standing_rules", "36a6cf48");

    config.AddCommand<EnrollCommand>("enroll")
        .WithDescription("Set up the owner TOTP secret and render its QR for an authenticator app.")
        .WithExample("enroll")
        .WithExample("enroll", "--new");
});

// No arguments → drop into the interactive hybrid shell. Otherwise run headless.
if (args.Length == 0)
{
    var shell = new InteractiveShell(app, registrar.Provider);
    return await shell.RunAsync(CancellationToken.None);
}

return await app.RunAsync(args);
