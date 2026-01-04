using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using Ancela.Agent.SemanticKernel.Plugins.MemoryPlugin;
using Ancela.Agent.SemanticKernel.Plugins.YnabPlugin;
using Ancela.Agent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ancela.Agent;

/// <summary>
/// Registers necessary services for the Agent feature.
/// </summary>
public static class DependencyModule
{
    public static IHostApplicationBuilder AddAncelaAgent(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Register Semantic Kernel plugins.
        builder.Services.AddSingleton<IGraphClient, GraphClient>();
        builder.Services.AddSingleton<GraphPlugin>();
        builder.Services.AddSingleton<IMemoryClient, MemoryClient>();
        builder.Services.AddSingleton<MemoryPlugin>();
        builder.Services.AddSingleton<YnabPlugin>();
        builder.Services.AddSingleton<YnabClient>();

        // Register core services.
        builder.Services.AddSingleton<Agent>();
        builder.Services.AddSingleton<IHistoryService, HistoryService>();
        builder.Services.AddSingleton<ISessionService, SessionService>();
        builder.Services.AddSingleton<SmsService>();
        builder.Services.AddSingleton<ChatInterceptor>();

        return builder;
    }
}
