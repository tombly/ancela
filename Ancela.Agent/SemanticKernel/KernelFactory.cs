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
using Ancela.Agent.SemanticKernel.Plugins.GoogleHealthPlugin;
using Ancela.Agent.SemanticKernel.Plugins.YnabPlugin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;

// Needed for IFunctionInvocationFilter
#pragma warning disable SKEXP0001

namespace Ancela.Agent.SemanticKernel;

public enum KernelProfile { Chat, Onboarding, StandingRule, ScheduledTask }

public interface IKernelFactory
{
    /// <summary>
    /// Builds a fresh <see cref="Kernel"/> for the given profile. Each call returns an
    /// independent instance with its own <see cref="Kernel.Data"/>, so per-request identity
    /// (agentPhoneNumber / userPhoneNumber) is never shared across concurrent invocations.
    /// </summary>
    Kernel Create(KernelProfile profile);
}

public sealed class KernelFactory(
    IServiceProvider _sp,
    GraphPlugin _graphPlugin,
    MemoryPlugin _memoryPlugin,
    ProjectsPlugin _projectsPlugin,
    YnabPlugin _ynabPlugin,
    GoogleHealthPlugin _googleHealthPlugin,
    ReminderPlugin _reminderPlugin,
    StandingRulePlugin _standingRulePlugin,
    ScheduledTaskPlugin _scheduledTaskPlugin,
    RegistrationPlugin _registrationPlugin,
    WebPlugin _webPlugin,
    SmsPlugin _smsPlugin,
    RemarkablePlugin _remarkablePlugin) : IKernelFactory
{
    public Kernel Create(KernelProfile profile)
    {
        var plugins = new KernelPluginCollection();

        switch (profile)
        {
            case KernelProfile.Onboarding:
                plugins.AddFromObject(_registrationPlugin);
                break;

            default:
                // Chat, StandingRule, ScheduledTask — all plugins are loaded.
                // Per-profile function-set restrictions are applied in Agent when
                // building OpenAIPromptExecutionSettings (A3).
                plugins.AddFromObject(_graphPlugin);
                plugins.AddFromObject(_memoryPlugin);
                plugins.AddFromObject(_projectsPlugin);
                plugins.AddFromObject(_ynabPlugin);
                plugins.AddFromObject(_googleHealthPlugin);
                plugins.AddFromObject(_reminderPlugin);
                plugins.AddFromObject(_standingRulePlugin);
                plugins.AddFromObject(_scheduledTaskPlugin);
                plugins.AddFromObject(_registrationPlugin);
                plugins.AddFromObject(_webPlugin);
                plugins.AddFromObject(_smsPlugin);
                plugins.AddFromObject(_remarkablePlugin);
                break;
        }

        var kernel = new Kernel(_sp, plugins);
        kernel.Data["profile"] = profile;

        foreach (var filter in _sp.GetServices<IFunctionInvocationFilter>())
            kernel.FunctionInvocationFilters.Add(filter);

        return kernel;
    }
}
