using System.Net.Http.Headers;
using System.Text;
using Ancela.Agent.SemanticKernel;
using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using Ancela.Agent.SemanticKernel.Plugins.MemoryPlugin;
using Ancela.Agent.SemanticKernel.Plugins.ProjectsPlugin;
using Ancela.Agent.SemanticKernel.Plugins.RegistrationPlugin;
using Ancela.Agent.SemanticKernel.Plugins.RemarkablePlugin;
using Ancela.Agent.SemanticKernel.Plugins.ReminderPlugin;
using Ancela.Agent.SemanticKernel.Plugins.ScheduledTaskPlugin;
using Ancela.Agent.SemanticKernel.Plugins.SmsPlugin;
using Ancela.Agent.SemanticKernel.Plugins.StandingRulePlugin;
using Ancela.Agent.SemanticKernel.Plugins.WebPlugin;
using Ancela.Agent.SemanticKernel.Plugins.YnabPlugin;
using Ancela.Agent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI;
using QuestPDF.Infrastructure;

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

        QuestPDF.Settings.License = LicenseType.Community;

        // Register core services.
        builder.Services.AddSingleton<Agent>();
        builder.Services.AddSingleton<ChatInterceptor>();
        builder.Services.AddSingleton<SmsService>();
        builder.Services.AddSingleton<IMediaService, MediaService>();
        builder.Services.AddSingleton<OwnerService>();
        builder.Services.AddSingleton<IHistoryService, HistoryService>();
        builder.Services.AddSingleton<IUserService, UserService>();
        builder.Services.AddSingleton<IAuditLog, CosmosAuditLog>();
        builder.Services.AddSingleton<CorrelationContext>();
        builder.Services.AddSingleton<IFunctionInvocationFilter, AuditFilter>();
        builder.Services.AddSingleton<IFunctionInvocationFilter, AutonomousToolGuardFilter>();

        // Register Semantic Kernel plugins. The plugins are registered as singletons so
        // that they can be re-used by multiple kernels. 
        builder.Services.AddSingleton<SmsPlugin>();
        builder.Services.AddSingleton<GraphPlugin>();
        builder.Services.AddSingleton<IGraphClient, GraphClient>();
        builder.Services.AddSingleton<MemoryPlugin>();
        builder.Services.AddSingleton<IMemoryClient, MemoryClient>();
        builder.Services.AddSingleton<ProjectsPlugin>();
        builder.Services.AddSingleton<IProjectStore, ProjectStore>();
        builder.Services.AddSingleton<YnabPlugin>();
        builder.Services.AddSingleton<YnabClient>();
        builder.Services.AddSingleton<ReminderPlugin>();
        builder.Services.AddSingleton<IReminderStore, ReminderStore>();
        builder.Services.AddSingleton<IReminderScheduler, ReminderScheduler>();
        builder.Services.AddSingleton<StandingRulePlugin>();
        builder.Services.AddSingleton<IStandingRuleStore, StandingRuleStore>();
        builder.Services.AddSingleton<IStandingRuleScheduler, StandingRuleScheduler>();
        builder.Services.AddSingleton<ScheduledTaskPlugin>();
        builder.Services.AddSingleton<IScheduledTaskStore, ScheduledTaskStore>();
        builder.Services.AddSingleton<IScheduledTaskScheduler, ScheduledTaskScheduler>();
        builder.Services.AddSingleton<RegistrationPlugin>();
        builder.Services.AddSingleton<WebPlugin>();
        builder.Services.AddSingleton<RemarkablePlugin>();
        builder.Services.AddSingleton<IRemarkableService, RemarkableService>();
        builder.Services.AddHttpClient("remarkable");
        builder.Services.AddSingleton<IKernelFactory, KernelFactory>();
        builder.Services.AddSingleton<ITavilyClient, TavilyClient>();
        builder.Services.AddHttpClient("tavily", client =>
        {
            client.BaseAddress = new Uri("https://api.tavily.com");
            var apiKey = Environment.GetEnvironmentVariable("TAVILY_API_KEY") ?? string.Empty;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        });

        // Twilio media URLs require HTTP Basic auth (account SID : auth token). The handler follows
        // Twilio's redirect to its pre-signed CDN and strips this header cross-origin automatically.
        builder.Services.AddHttpClient(MediaService.TwilioMediaClientName, client =>
        {
            var accountSid = Environment.GetEnvironmentVariable("TWILIO_ACCOUNT_SID") ?? string.Empty;
            var authToken = Environment.GetEnvironmentVariable("TWILIO_AUTH_TOKEN") ?? string.Empty;
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{accountSid}:{authToken}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        });

        // Register a chat completion service for use by the kernels.
        builder.Services.AddSingleton<IChatCompletionService>(sp =>
        {
            return new OpenAIChatCompletionService("gpt-5-mini", sp.GetRequiredService<OpenAIClient>());
        });

        return builder;
    }
}
