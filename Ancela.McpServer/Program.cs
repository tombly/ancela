using Ancela.Agent.SemanticKernel.Plugins.MemoryPlugin;
using Ancela.McpServer;
using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// stdout IS the JSON-RPC channel. The default console logger writes there, which corrupts every
// response, so every log line goes to stderr — where Claude Desktop collects it into its MCP log.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

var endpoint = AncelaEnvironment.ResolveCosmosEndpoint()
    ?? throw new InvalidOperationException(
        "Set ANCELA_COSMOS_ENDPOINT (or ANCELA_RESOURCE_PREFIX) so the server knows which Cosmos account to read.");

builder.Services.AddSingleton(new CosmosClient(
    endpoint,
    new DefaultAzureCredential(),
    new CosmosClientOptions { ApplicationName = "ancela-mcp" }));

builder.Services.AddSingleton<IMemoryClient, MemoryClient>();
builder.Services.AddSingleton<AncelaEnvironment>();

builder.Services
    .AddMcpServer(options => options.ServerInfo = new() { Name = "ancela", Version = "0.1.0" })
    .WithStdioServerTransport()
    .WithTools<MemoryTools>();

await builder.Build().RunAsync();
