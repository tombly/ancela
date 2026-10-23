using System.ComponentModel;
using Ancela.Agent.Services;
using Ancela.Cli.Chat;
using Ancela.Cli.Infrastructure;
using Azure;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ancela.Cli.Commands;

public sealed class ChatSettings : ConnectionSettings
{
    [CommandArgument(0, "[message]")]
    [Description("Send one message, print the reply and exit. Without it, start a conversation.")]
    public string? Message { get; init; }

    [CommandOption("--servicebus <NAMESPACE>")]
    [Description("Service Bus namespace host. Defaults to $ANCELA_SERVICEBUS_NAMESPACE, then derived from $ANCELA_RESOURCE_PREFIX.")]
    public string? ServiceBus { get; init; }
}

/// <summary>
/// Talk to Ancela from the terminal, for when SMS can't reach you: on a plane, or out of signal.
/// Signs in with your az login, like every other command; sending needs the Azure Service Bus Data
/// Sender role on the chat-messages queue.
/// </summary>
public sealed class ChatCommand(CosmosBrowser _browser) : AsyncCommand<ChatSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ChatSettings settings, CancellationToken cancellationToken)
    {
        var endpoint = CosmosClientProvider.ResolveEndpoint(settings.Endpoint);
        if (endpoint is null)
            return Errors.NoEndpoint();

        var serviceBus = ResolveServiceBus(settings.ServiceBus);
        if (serviceBus is null)
        {
            AnsiConsole.MarkupLine("[red]No Service Bus namespace.[/] Pass [yellow]--servicebus[/], or set " +
                "[yellow]ANCELA_SERVICEBUS_NAMESPACE[/] / [yellow]ANCELA_RESOURCE_PREFIX[/].");
            return 1;
        }

        var userPhoneNumber = Environment.GetEnvironmentVariable("ANCELA_OWNER_PHONE_NUMBER");
        if (string.IsNullOrWhiteSpace(userPhoneNumber))
        {
            AnsiConsole.MarkupLine("[red]Who are you?[/] Set [yellow]ANCELA_OWNER_PHONE_NUMBER[/] to the number you text Ancela from, e.g. +15551234567.");
            return 1;
        }

        string? agentPhoneNumber;
        UserProfile? profile;
        try
        {
            agentPhoneNumber = Environment.GetEnvironmentVariable("ANCELA_AGENT_PHONE_NUMBER") is { Length: > 0 } fromEnv
                ? fromEnv
                : await ChatFeed.DiscoverAgentPhoneNumberAsync(_browser, endpoint, cancellationToken);
            if (agentPhoneNumber is null)
            {
                AnsiConsole.MarkupLine("[red]Couldn't tell which Ancela number to use.[/] Set [yellow]ANCELA_AGENT_PHONE_NUMBER[/].");
                return 1;
            }
            profile = await new ChatFeed(_browser, endpoint, agentPhoneNumber, userPhoneNumber).ProfileAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is Microsoft.Azure.Cosmos.CosmosException or AuthenticationFailedException or CredentialUnavailableException)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Couldn't read Cosmos:[/] {ex.Message.Split('\n')[0]}");
            AnsiConsole.MarkupLine("[grey]Is your [yellow]az login[/] current?[/]");
            return 1;
        }

        if (profile?.Name is null)
        {
            AnsiConsole.MarkupLine("[red]That number isn't registered with Ancela.[/] Text [yellow]hello ancela[/] from it first.");
            return 1;
        }

        var timeZone = profile.TimeZone is { } tz && TimeZoneInfo.TryFindSystemTimeZoneById(tz, out var found)
            ? found
            : TimeZoneInfo.Local;

        await using var client = new ServiceBusClient(serviceBus, CliCredential.Instance);
        await using var sender = client.CreateSender(ChatQueueMessage.QueueName);
        var session = new ChatSession(
            new ChatFeed(_browser, endpoint, agentPhoneNumber, userPhoneNumber),
            sender, ChatState.Load(), agentPhoneNumber, userPhoneNumber, profile.Name, timeZone);

        try
        {
            return await session.RunAsync(settings.Message, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            NoSendRole(serviceBus);
            return 1;
        }
        catch (ServiceBusException ex) when (ex.InnerException is UnauthorizedAccessException)
        {
            NoSendRole(serviceBus);
            return 1;
        }
        catch (RequestFailedException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Azure refused the request:[/] {ex.Message.Split('\n')[0]}");
            return 1;
        }
    }

    /// <summary>
    /// The namespace host, in priority order: explicit --servicebus, ANCELA_SERVICEBUS_NAMESPACE,
    /// then derived from ANCELA_RESOURCE_PREFIX (FixedNameInfrastructureResolver's naming).
    /// </summary>
    public static string? ResolveServiceBus(string? cliOverride)
    {
        if (!string.IsNullOrWhiteSpace(cliOverride))
            return cliOverride;

        var fromEnv = Environment.GetEnvironmentVariable("ANCELA_SERVICEBUS_NAMESPACE");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv;

        var prefix = Environment.GetEnvironmentVariable("ANCELA_RESOURCE_PREFIX");
        return string.IsNullOrWhiteSpace(prefix) ? null : $"{prefix}-servicebus.servicebus.windows.net";
    }

    private static void NoSendRole(string serviceBus)
    {
        AnsiConsole.MarkupLineInterpolated($"[red]Your az login can't send to the chat-messages queue on {serviceBus}.[/]");
        AnsiConsole.MarkupLine("[grey]It needs the [yellow]Azure Service Bus Data Sender[/] role on that queue. See the README's CLI section.[/]");
    }
}
