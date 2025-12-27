using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using Ancela.Agent.SemanticKernel.Plugins.MemoryPlugin;
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

        builder.Services.AddSingleton<IGraphClient, GraphClient>();
        builder.Services.AddSingleton<GraphPlugin>();
        builder.Services.AddSingleton<IKnowledgeClient, KnowledgeClient>();
        builder.Services.AddSingleton<ITodoClient, TodoClient>();
        builder.Services.AddSingleton<MemoryPlugin>();

        builder.Services.AddSingleton<ChatService>();
        builder.Services.AddSingleton<IHistoryService, HistoryService>();
        builder.Services.AddSingleton<ISessionService, SessionService>();
        builder.Services.AddSingleton<SmsService>();
        builder.Services.AddSingleton<CommandInterceptor>();

        return builder;
    }
}
