using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ancela.Cli.Commands;

/// <summary>Base settings shared by every command that talks to Cosmos.</summary>
public class ConnectionSettings : CommandSettings
{
    [CommandOption("--endpoint <URL>")]
    [Description("Cosmos account endpoint. Defaults to $ANCELA_COSMOS_ENDPOINT, then derived from $ANCELA_RESOURCE_PREFIX.")]
    public string? Endpoint { get; init; }
}
