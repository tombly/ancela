using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using Ancela.Agent.SemanticKernel.Plugins.MemoryPlugin;
using Ancela.Agent.SemanticKernel.Plugins.ReminderPlugin;
using Ancela.Agent.SemanticKernel.Plugins.SmsPlugin;
using Ancela.Agent.SemanticKernel.Plugins.YnabPlugin;
using Ancela.Agent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI;

// Needed for IFunctionInvocationFilter registration
#pragma warning disable SKEXP0001

namespace Ancela.Agent;

/// <summary>
/// Registers necessary services for the Agent.
/// </summary>
public static class DependencyModule
{
    public static IHostApplicationBuilder AddAncelaAgent(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        AppContext.SetSwitch("Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnostics", true);

        // Register core services.
        builder.Services.AddSingleton<Agent>();
        builder.Services.AddSingleton<ChatInterceptor>();
        builder.Services.AddSingleton<SmsService>();
        builder.Services.AddSingleton<IHistoryService, HistoryService>();
        builder.Services.AddSingleton<ISessionService, SessionService>();
        builder.Services.AddSingleton<IAuditLog, CosmosAuditLog>();
        builder.Services.AddSingleton<CorrelationContext>();
        builder.Services.AddSingleton<IFunctionInvocationFilter, AuditFilter>();

        // Register Semantic Kernel plugins. The plugins are registered as singletons so
        // that they can be re-used by multiple kernels. 
        builder.Services.AddSingleton<SmsPlugin>();
        builder.Services.AddSingleton<GraphPlugin>();
        builder.Services.AddSingleton<IGraphClient, GraphClient>();
        builder.Services.AddSingleton<MemoryPlugin>();
        builder.Services.AddSingleton<IMemoryClient, MemoryClient>();
        builder.Services.AddSingleton<YnabPlugin>();
        builder.Services.AddSingleton<YnabClient>();
        builder.Services.AddSingleton<ReminderPlugin>();
        builder.Services.AddSingleton<IReminderStore, ReminderStore>();
        builder.Services.AddSingleton<IReminderScheduler, ReminderScheduler>();

        // Register a chat completion service for use by the kernels.
        builder.Services.AddSingleton<IChatCompletionService>(sp =>
        {
            return new OpenAIChatCompletionService("gpt-5-mini", sp.GetRequiredService<OpenAIClient>());
        });

        // Use transient so that we get a new kernel instance for each request
        // since instances are stateful.
        builder.Services.AddTransient((sp) =>
        {
            var pluginCollection = new KernelPluginCollection();
            pluginCollection.AddFromObject(sp.GetRequiredService<GraphPlugin>());
            pluginCollection.AddFromObject(sp.GetRequiredService<MemoryPlugin>());
            pluginCollection.AddFromObject(sp.GetRequiredService<YnabPlugin>());
            pluginCollection.AddFromObject(sp.GetRequiredService<ReminderPlugin>());
            pluginCollection.AddFromObject(sp.GetRequiredService<SmsPlugin>());
            var kernel = new Kernel(sp, pluginCollection);
            foreach (var filter in sp.GetServices<IFunctionInvocationFilter>())
                kernel.FunctionInvocationFilters.Add(filter);
            return kernel;
        });

        return builder;
    }
}
