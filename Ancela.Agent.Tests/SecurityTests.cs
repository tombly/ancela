using Ancela.Agent.SemanticKernel;
using Ancela.Agent.SemanticKernel.Plugins.DiagnosticsPlugin;
using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using Ancela.Agent.SemanticKernel.Plugins.InstructionsPlugin;
using Ancela.Agent.SemanticKernel.Plugins.MemoryPlugin;
using Ancela.Agent.SemanticKernel.Plugins.ProjectsPlugin;
using Ancela.Agent.SemanticKernel.Plugins.RegistrationPlugin;
using Ancela.Agent.SemanticKernel.Plugins.RemarkablePlugin;
using Ancela.Agent.SemanticKernel.Plugins.ReminderPlugin;
using Ancela.Agent.SemanticKernel.Plugins.ScheduledTaskPlugin;
using Ancela.Agent.SemanticKernel.Plugins.SmsPlugin;
using Ancela.Agent.SemanticKernel.Plugins.StandingRulePlugin;
using Ancela.Agent.SemanticKernel.Plugins.StandingRulePlugin.Models;
using Ancela.Agent.SemanticKernel.Plugins.GoogleHealthPlugin;
using Ancela.Agent.SemanticKernel.Plugins.WebPlugin;
using Ancela.Agent.SemanticKernel.Plugins.UnifiPlugin;
using Ancela.Agent.SemanticKernel.Plugins.WeatherPlugin;
using Ancela.Agent.SemanticKernel.Plugins.YnabPlugin;
using Ancela.Agent.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Moq;

#pragma warning disable SKEXP0001

namespace Ancela.Agent.Tests;

/// <summary>
/// Security tests verifying the per-profile function restrictions introduced in the v2
/// security review (Finding A: prompt-injection exfiltration; Finding B: kernel race).
/// These tests do NOT require a live OpenAI key.
/// </summary>
public class SecurityTests
{
    public SecurityTests()
    {
        // SmsService (and SpySmsService which derives from it) reads these at construction time.
        Environment.SetEnvironmentVariable("TWILIO_PHONE_NUMBER", "+10000000000");
        Environment.SetEnvironmentVariable("TWILIO_ACCOUNT_SID", "ACXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX");
        Environment.SetEnvironmentVariable("TWILIO_AUTH_TOKEN", "test-token");
        Environment.SetEnvironmentVariable("YNAB_ACCESS_TOKEN", "test-token");
        Environment.SetEnvironmentVariable("OWNER_PHONE_NUMBER", "+15551234567");
    }

    // Functions that must never appear in autonomous kernel profiles.
    private static readonly HashSet<string> Denied = new(StringComparer.OrdinalIgnoreCase)
    {
        "send_sms", "send_email",
        "create_calendar_event",
        "register_user",
        "save_todo", "delete_todo",
        "save_knowledge", "delete_knowledge",
        "create_reminder", "cancel_reminder",
        "create_standing_rule", "pause_standing_rule", "resume_standing_rule", "delete_standing_rule",
        "create_scheduled_task", "pause_scheduled_task", "resume_scheduled_task", "delete_scheduled_task",
        "check_services", "check_anomalies",  // self-check is owner-initiated, on-demand only
        // An instruction goes into every later Chat prompt; a background run must not set one.
        "remember_instruction", "forget_instruction",
        // Read-only, but presence data: who is home is not something a standing rule or
        // scheduled task should be able to read and put into a message.
        "get_network_client_count", "get_network_uptime",
    };

    private static IKernelFactory BuildFactory()
    {
        Environment.SetEnvironmentVariable("TWILIO_PHONE_NUMBER", "+10000000000");
        Environment.SetEnvironmentVariable("TWILIO_ACCOUNT_SID", "ACXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX");
        Environment.SetEnvironmentVariable("TWILIO_AUTH_TOKEN", "test-token");
        Environment.SetEnvironmentVariable("YNAB_ACCESS_TOKEN", "test-token");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IGraphClient>(_ => new Mock<IGraphClient>().Object);
        services.AddSingleton<IMemoryClient>(_ => new Mock<IMemoryClient>().Object);
        services.AddSingleton<SmsService, SpySmsService>();
        services.AddSingleton<OwnerService>();
        services.AddSingleton<YnabClient>();
        services.AddSingleton<GraphPlugin>();
        services.AddSingleton<MemoryPlugin>();
        services.AddSingleton<SmsPlugin>();
        services.AddSingleton<YnabPlugin>();
        services.AddSingleton<GoogleHealthPlugin>(sp => new GoogleHealthPlugin(
            new GoogleHealthClient(
                new Mock<IHttpClientFactory>().Object,
                new Mock<IGoogleHealthTokenStore>().Object,
                new GoogleHealthCredentials(),
                NullLogger<GoogleHealthClient>.Instance)));
        services.AddSingleton<ReminderPlugin>(sp =>
            new ReminderPlugin(
                new Mock<IReminderStore>().Object,
                new Mock<IReminderScheduler>().Object,
                NullLogger<ReminderPlugin>.Instance));
        services.AddSingleton<StandingRulePlugin>(sp =>
            new StandingRulePlugin(
                new Mock<IStandingRuleStore>().Object,
                new Mock<IStandingRuleScheduler>().Object,
                NullLogger<StandingRulePlugin>.Instance));
        services.AddSingleton<ScheduledTaskPlugin>(sp =>
            new ScheduledTaskPlugin(
                new Mock<IScheduledTaskStore>().Object,
                new Mock<IScheduledTaskScheduler>().Object,
                new Mock<IUserService>().Object,
                NullLogger<ScheduledTaskPlugin>.Instance));
        services.AddSingleton<RegistrationPlugin>(sp =>
            new RegistrationPlugin(
                new Mock<IUserService>().Object,
                new Mock<IAuditLog>().Object,
                new CorrelationContext()));
        services.AddSingleton<ITavilyClient>(_ => new Mock<ITavilyClient>().Object);
        services.AddSingleton<ProjectsPlugin>(sp => new ProjectsPlugin(new Mock<IProjectStore>().Object));
        services.AddSingleton<WebPlugin>();
        services.AddSingleton<IRemarkableService>(_ => new Mock<IRemarkableService>().Object);
        services.AddSingleton<RemarkablePlugin>();
        services.AddSingleton<IServiceHealthChecker>(_ => new Mock<IServiceHealthChecker>().Object);
        services.AddSingleton<IAuditAnomalyScanner>(_ => new Mock<IAuditAnomalyScanner>().Object);
        services.AddSingleton<DiagnosticsPlugin>();
        services.AddSingleton<IUnifiClient>(_ => new Mock<IUnifiClient>().Object);
        services.AddSingleton<UnifiPlugin>();
        services.AddSingleton<IWeatherClient>(_ => new Mock<IWeatherClient>().Object);
        services.AddSingleton<WeatherPlugin>();
        services.AddSingleton<InstructionsPlugin>(_ => new InstructionsPlugin(new Mock<IUserService>().Object));
        services.AddSingleton<IFunctionInvocationFilter, AuditFilter>();
        services.AddSingleton<IFunctionInvocationFilter, AutonomousToolGuardFilter>();
        services.AddSingleton<IFunctionInvocationFilter, EmptyResultFilter>();
        services.AddSingleton<IAuditLog>(_ => new Mock<IAuditLog>().Object);
        services.AddSingleton<CorrelationContext>();
        services.AddSingleton<IKernelFactory, KernelFactory>();

        return services.BuildServiceProvider().GetRequiredService<IKernelFactory>();
    }

    // --- A3: Profile policy (the single source of truth Agent + filter both consume) ---

    [Theory]
    [InlineData(KernelProfile.StandingRule)]
    [InlineData(KernelProfile.ScheduledTask)]
    public void AutonomousProfile_AllowsNoDeniedFunctions(KernelProfile profile)
    {
        var allowed = KernelProfilePolicy.AllowedFunctions(profile);
        allowed.Should().NotBeNull(because: $"{profile} is a restricted profile");

        var violations = Denied.Intersect(allowed!).ToList();
        violations.Should().BeEmpty(
            because: $"Profile {profile} must not allow write/send functions, but found: {string.Join(", ", violations)}");
    }

    [Theory]
    [InlineData(KernelProfile.Chat)]
    [InlineData(KernelProfile.Onboarding)]
    public void HumanInLoopProfile_IsUnrestricted(KernelProfile profile)
    {
        // null == no restriction: every loaded function is allowed (a human reviews the action).
        KernelProfilePolicy.AllowedFunctions(profile).Should().BeNull();
    }

    [Fact]
    public void UndefinedProfile_IsNotUnrestricted()
    {
        // null would mean "every loaded function is allowed". KernelFactory's own switch loads
        // all plugins for an unrecognized profile, so returning null here would have made a cast
        // integer the most privileged caller in the system.
        var allowed = KernelProfilePolicy.AllowedFunctions((KernelProfile)999);

        allowed.Should().NotBeNull(because: "an unrecognized profile must not be unrestricted");
        allowed!.Should().BeEmpty(because: "an unrecognized profile allows nothing at all");
        KernelProfilePolicy.IsAllowed((KernelProfile)999, "get_todos").Should().BeFalse(
            because: "deny-all covers harmless reads too — the profile is the thing in doubt");
    }

    [Fact]
    public void StandingRuleProfile_ExcludesEmailAndCalendar()
    {
        // Email and calendar content are untrusted input channels (external parties control them).
        // Standing rules check external conditions (prices, news) and don't need them.
        var allowed = KernelProfilePolicy.AllowedFunctions(KernelProfile.StandingRule)!;

        allowed.Should().NotContain("get_recent_emails",
            because: "email bodies are attacker-controlled input and standing rules don't need them");
        allowed.Should().NotContain("read_email",
            because: "email bodies are attacker-controlled input and standing rules don't need them");
        allowed.Should().NotContain("get_calendar_events",
            because: "calendar event descriptions are attacker-controlled input and standing rules don't need them");
    }

    [Fact]
    public void ScheduledTaskProfile_IncludesEmailAndCalendar()
    {
        // Scheduled tasks like "daily calendar summary" legitimately need these.
        // The system prompt explicitly marks their content as untrusted data.
        var allowed = KernelProfilePolicy.AllowedFunctions(KernelProfile.ScheduledTask)!;

        allowed.Should().Contain("get_recent_emails",
            because: "scheduled tasks like 'email digest' legitimately need email access");
        allowed.Should().Contain("read_email",
            because: "a digest can't say what an email says from its first 255 characters");
        allowed.Should().Contain("get_calendar_events",
            because: "scheduled tasks like 'calendar summary' legitimately need calendar access");
    }

    [Theory]
    [InlineData(KernelProfile.Chat)]
    [InlineData(KernelProfile.Onboarding)]
    [InlineData(KernelProfile.StandingRule)]
    [InlineData(KernelProfile.ScheduledTask)]
    public void KernelFactory_AttachesEachFilterOnce(KernelProfile profile)
    {
        // new Kernel(services, ...) adds the filters registered in services itself. KernelFactory
        // used to add them again, so each ran twice and every audit entry was written twice,
        // doubling the counts check_anomalies compares against its thresholds.
        var filters = BuildFactory().Create(profile).FunctionInvocationFilters;

        filters.OfType<AuditFilter>().Should().ContainSingle();
        filters.OfType<AutonomousToolGuardFilter>().Should().ContainSingle();
        filters.OfType<EmptyResultFilter>().Should().ContainSingle();
        // Innermost, so AuditFilter records the "found nothing" message the model is shown.
        filters.Last().Should().BeOfType<EmptyResultFilter>();
    }

    [Theory]
    [InlineData(KernelProfile.StandingRule)]
    [InlineData(KernelProfile.ScheduledTask)]
    public void AllowedFunctionNames_AllMapToRealFunctions(KernelProfile profile)
    {
        // Guards against drift/typos: every name in the allow-list must correspond to a real
        // KernelFunction, otherwise the advertise-only restriction silently lets nothing through
        // for that name and we'd never notice.
        var factory = BuildFactory();
        var loaded = factory.Create(KernelProfile.Chat).Plugins
            .SelectMany(p => p)
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unknown = KernelProfilePolicy.AllowedFunctions(profile)!.Where(n => !loaded.Contains(n)).ToList();
        unknown.Should().BeEmpty(
            because: $"every allowed name must map to a real function, but these don't: {string.Join(", ", unknown)}");
    }

    [Theory]
    [InlineData(KernelProfile.StandingRule)]
    [InlineData(KernelProfile.ScheduledTask)]
    public void AutonomousProfile_AdvertisedSet_ExcludesDeniedAndKeepsReads(KernelProfile profile)
    {
        // Mirrors Agent.InvokeKernelAsync's advertise computation against a real, fully-loaded
        // kernel: the function set handed to the model is allow-list ∩ loaded functions.
        var factory = BuildFactory();
        var kernel = factory.Create(profile);
        var allowedNames = KernelProfilePolicy.AllowedFunctions(profile)!;

        var advertised = kernel.Plugins
            .SelectMany(p => p)
            .Where(f => allowedNames.Contains(f.Name))
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Denied.Intersect(advertised).Should().BeEmpty(
            because: "no write/send function may be advertised to an autonomous run");
        advertised.Should().Contain("web_search",
            because: "autonomous runs still need read-only investigative tools");
    }

    // --- A3: AutonomousToolGuardFilter hard-deny tests (injection red-team) ---

    [Theory]
    [InlineData(KernelProfile.StandingRule, "send_sms")]
    [InlineData(KernelProfile.StandingRule, "send_email")]
    [InlineData(KernelProfile.StandingRule, "create_calendar_event")]
    [InlineData(KernelProfile.StandingRule, "list_reminders")]  // not write/send, but still not allow-listed → default-deny
    [InlineData(KernelProfile.StandingRule, "save_email_to_onedrive")]
    [InlineData(KernelProfile.ScheduledTask, "send_sms")]
    [InlineData(KernelProfile.ScheduledTask, "register_user")]
    [InlineData(KernelProfile.ScheduledTask, "save_email_to_onedrive")]  // may read email, not copy it out
    [InlineData(KernelProfile.StandingRule, "send_email_to_remarkable")]
    [InlineData(KernelProfile.ScheduledTask, "send_email_to_remarkable")]
    public async Task AutonomousToolGuardFilter_BlocksNonAllowedFunction(KernelProfile profile, string functionName)
    {
        // The filter throws InvalidOperationException, which Semantic Kernel propagates unwrapped.
        var kernel = new Kernel();
        kernel.Data["profile"] = profile;
        kernel.FunctionInvocationFilters.Add(
            new AutonomousToolGuardFilter(NullLogger<AutonomousToolGuardFilter>.Instance));

        var fn = KernelFunctionFactory.CreateFromMethod(() => "would-exfiltrate", functionName: functionName);
        kernel.Plugins.AddFromFunctions("TestPlugin", [fn]);

        var act = async () => await kernel.InvokeAsync(fn);

        await act.Should().ThrowAsync<InvalidOperationException>(
            because: $"AutonomousToolGuardFilter must block '{functionName}' in the {profile} profile");
    }

    [Theory]
    [InlineData(null)]              // key absent entirely
    [InlineData("not-a-profile")]   // key present but the wrong type
    [InlineData((KernelProfile)999)]  // a defined type but an undefined value
    public async Task AutonomousToolGuardFilter_WithoutValidProfile_FallsBackToDenying(object? profileValue)
    {
        // The fallback must not be Chat. Chat is unrestricted, so a backstop that defaults to it
        // opens the gate in exactly the case where it has lost track of what it is guarding.
        // KernelFactory always sets a valid profile, so this is latent-safety, not a live path.
        var kernel = new Kernel();
        if (profileValue is not null)
            kernel.Data["profile"] = profileValue;
        kernel.Data["isOwner"] = true;
        kernel.FunctionInvocationFilters.Add(
            new AutonomousToolGuardFilter(NullLogger<AutonomousToolGuardFilter>.Instance));

        var fn = KernelFunctionFactory.CreateFromMethod(() => "would-exfiltrate", functionName: "send_sms");
        kernel.Plugins.AddFromFunctions("TestPlugin", [fn]);

        var act = async () => await kernel.InvokeAsync(fn);

        await act.Should().ThrowAsync<InvalidOperationException>(
            because: "an unresolvable profile must deny, even for the owner");
    }

    [Fact]
    public async Task AutonomousToolGuardFilter_WithNullProfileValue_FallsBackToDenying()
    {
        // Distinct from the absent/wrong-type cases: the key exists holding null, which used to
        // unbox straight into a NullReferenceException rather than a denial.
        var kernel = new Kernel();
        kernel.Data["profile"] = null;
        kernel.Data["isOwner"] = true;
        kernel.FunctionInvocationFilters.Add(
            new AutonomousToolGuardFilter(NullLogger<AutonomousToolGuardFilter>.Instance));

        var fn = KernelFunctionFactory.CreateFromMethod(() => "would-exfiltrate", functionName: "send_sms");
        kernel.Plugins.AddFromFunctions("TestPlugin", [fn]);

        var act = async () => await kernel.InvokeAsync(fn);

        await act.Should().ThrowAsync<InvalidOperationException>(
            because: "a null profile value must deny rather than throw NullReferenceException");
    }

    [Fact]
    public async Task AutonomousToolGuardFilter_AllowsReadFunction()
    {
        var kernel = new Kernel();
        kernel.Data["profile"] = KernelProfile.StandingRule;
        kernel.FunctionInvocationFilters.Add(
            new AutonomousToolGuardFilter(NullLogger<AutonomousToolGuardFilter>.Instance));

        var fn = KernelFunctionFactory.CreateFromMethod(() => "[]", functionName: "web_search");
        kernel.Plugins.AddFromFunctions("TestPlugin", [fn]);

        var result = await kernel.InvokeAsync(fn);
        result.GetValue<string>().Should().Be("[]", because: "web_search is allowed in autonomous profiles");
    }

    [Fact]
    public async Task AutonomousToolGuardFilter_AllowsOwnerOnlyFunction_ForOwnerInChatProfile()
    {
        // Chat is human-in-the-loop and unrestricted for the owner — send_sms must pass.
        var kernel = new Kernel();
        kernel.Data["profile"] = KernelProfile.Chat;
        kernel.Data["isOwner"] = true;
        kernel.FunctionInvocationFilters.Add(
            new AutonomousToolGuardFilter(NullLogger<AutonomousToolGuardFilter>.Instance));

        var fn = KernelFunctionFactory.CreateFromMethod(() => "sent", functionName: "send_sms");
        kernel.Plugins.AddFromFunctions("TestPlugin", [fn]);

        var result = await kernel.InvokeAsync(fn);
        result.GetValue<string>().Should().Be("sent");
    }

    [Theory]
    [InlineData("send_sms")]
    [InlineData("send_email")]
    [InlineData("create_calendar_event")]
    [InlineData("check_services")]
    [InlineData("check_anomalies")]
    [InlineData("get_network_client_count")]
    [InlineData("get_network_uptime")]
    [InlineData("save_email_to_onedrive")]
    [InlineData("send_email_to_remarkable")]
    public async Task AutonomousToolGuardFilter_BlocksOwnerOnlyFunction_ForNonOwnerInChatProfile(string functionName)
    {
        // A non-owner registered user is read-only: owner-only functions are hard-denied even
        // in the otherwise-unrestricted Chat profile. Missing/false isOwner == not-owner.
        var kernel = new Kernel();
        kernel.Data["profile"] = KernelProfile.Chat;
        kernel.Data["isOwner"] = false;
        kernel.FunctionInvocationFilters.Add(
            new AutonomousToolGuardFilter(NullLogger<AutonomousToolGuardFilter>.Instance));

        var fn = KernelFunctionFactory.CreateFromMethod(() => "would-act-as-owner", functionName: functionName);
        kernel.Plugins.AddFromFunctions("TestPlugin", [fn]);

        var act = async () => await kernel.InvokeAsync(fn);

        await act.Should().ThrowAsync<InvalidOperationException>(
            because: $"non-owners must not be able to invoke owner-only '{functionName}'");
    }

    [Fact]
    public void OwnerOnlyFunctions_AreReadDeniedToNonOwners_ButReadsAreNot()
    {
        // The owner-only axis covers send/write actions; read functions stay open to all users.
        KernelProfilePolicy.IsOwnerOnly("send_sms").Should().BeTrue();
        KernelProfilePolicy.IsOwnerOnly("send_email").Should().BeTrue();
        KernelProfilePolicy.IsOwnerOnly("create_calendar_event").Should().BeTrue();
        KernelProfilePolicy.IsOwnerOnly("check_services").Should().BeTrue(
            because: "the self-check reveals operational/security posture and is owner-only");
        KernelProfilePolicy.IsOwnerOnly("check_anomalies").Should().BeTrue(
            because: "the self-check reveals operational/security posture and is owner-only");
        KernelProfilePolicy.IsOwnerOnly("get_network_client_count").Should().BeTrue(
            because: "who is connected at home is presence data");
        KernelProfilePolicy.IsOwnerOnly("get_network_uptime").Should().BeTrue(
            because: "home network status is owner-private");
        KernelProfilePolicy.IsOwnerOnly("save_email_to_onedrive").Should().BeTrue(
            because: "it writes the owner's email into the owner's OneDrive");
        KernelProfilePolicy.IsOwnerOnly("send_email_to_remarkable").Should().BeTrue(
            because: "it sends the owner's email to the owner's tablet");

        KernelProfilePolicy.IsOwnerOnly("get_recent_emails").Should().BeFalse();
        KernelProfilePolicy.IsOwnerOnly("read_email").Should().BeFalse();
        KernelProfilePolicy.IsOwnerOnly("get_calendar_events").Should().BeFalse();
        KernelProfilePolicy.IsOwnerOnly("get_contacts").Should().BeFalse();
        KernelProfilePolicy.IsOwnerOnly("web_search").Should().BeFalse();
    }

    // --- Untrusted content within one message ---

    [Theory]
    [InlineData("get_recent_emails", "send_email")]
    [InlineData("get_recent_emails", "send_sms")]
    [InlineData("get_recent_emails", "web_fetch")]  // a URL the model picks can carry the inbox out
    [InlineData("get_recent_emails", "remember_instruction")]
    [InlineData("get_recent_emails", "forget_instruction")]
    [InlineData("read_email", "send_email")]
    [InlineData("read_email", "send_sms")]
    [InlineData("read_email", "web_fetch")]
    [InlineData("read_email", "remember_instruction")]
    [InlineData("get_calendar_events", "send_email")]
    [InlineData("get_calendar_events", "web_fetch")]
    [InlineData("web_search", "send_email")]
    [InlineData("web_search", "send_sms")]
    [InlineData("web_search", "remember_instruction")]
    [InlineData("web_fetch", "send_email")]
    public async Task UntrustedRead_SwitchesOffForTheRestOfTheMessage(string read, string then)
    {
        // Chat, owner: the most permissive caller there is, so only this rule can deny.
        var kernel = GuardedChatKernel();
        await InvokeAsync(kernel, read);

        var act = async () => await InvokeAsync(kernel, then);

        await act.Should().ThrowAsync<InvalidOperationException>(
                because: $"once {read} has put text other people wrote in front of the model, {then} could be acting on it")
            .WithMessage($"*{AutonomousToolGuardFilter.UntrustedContentDenialMarker}*",
                because: "check_anomalies counts guard denials by this text");
    }

    [Theory]
    [InlineData("get_recent_emails", "save_email_to_onedrive")]  // writes only into the owner's folder
    [InlineData("get_recent_emails", "send_email_to_remarkable")]  // sends only to the owner's tablet
    [InlineData("get_recent_emails", "create_calendar_event")]   // no attendees, so no invitation goes out
    [InlineData("get_recent_emails", "get_recent_emails")]
    [InlineData("get_recent_emails", "read_email")]
    [InlineData("read_email", "save_email_to_onedrive")]
    [InlineData("web_search", "web_fetch")]                      // opening a result is what search is for
    [InlineData("web_fetch", "web_fetch")]
    [InlineData("get_contacts", "send_email")]                   // the user's own address book is not outside text
    public async Task UntrustedRead_LeavesOtherFunctionsOn(string read, string then)
    {
        var kernel = GuardedChatKernel();
        await InvokeAsync(kernel, read);

        var result = await InvokeAsync(kernel, then);

        result.GetValue<string>().Should().Be("ok", because: $"{read} does not switch {then} off");
    }

    [Fact]
    public async Task UntrustedRead_CountsEvenWhenTheReadFails()
    {
        // A read can fail after part of its text has reached the model (or an error message can
        // quote it), so the mark goes on before the read runs.
        var kernel = GuardedChatKernel();
        var failingRead = KernelFunctionFactory.CreateFromMethod(
            string () => throw new HttpRequestException("Graph timed out"), functionName: "get_recent_emails");
        await ((Func<Task>)(async () => await kernel.InvokeAsync(failingRead))).Should().ThrowAsync<HttpRequestException>();

        var act = async () => await InvokeAsync(kernel, "send_email");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UntrustedRead_DoesNotCarryIntoTheNextMessage()
    {
        // Each message gets its own kernel, so the user confirming in a new message is what
        // switches sending back on.
        var factory = BuildFactory();
        var first = factory.Create(KernelProfile.Chat);
        first.Data["isOwner"] = true;
        await InvokeAsync(first, "get_recent_emails");
        await ((Func<Task>)(async () => await InvokeAsync(first, "send_email"))).Should().ThrowAsync<InvalidOperationException>();

        var next = factory.Create(KernelProfile.Chat);
        next.Data["isOwner"] = true;
        var result = await InvokeAsync(next, "send_email");

        result.GetValue<string>().Should().Be("ok", because: "the next message has read nothing yet");
    }

    [Fact]
    public void UntrustedContentFunctionNames_AllMapToRealFunctions()
    {
        // A misspelt name here would quietly switch nothing off.
        var factory = BuildFactory();
        var loaded = factory.Create(KernelProfile.Chat).Plugins
            .SelectMany(p => p)
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unknown = KernelProfilePolicy.UntrustedContentFunctionNames.Where(n => !loaded.Contains(n)).ToList();
        unknown.Should().BeEmpty(
            because: $"every name in the untrusted-content rules must map to a real function, but these don't: {string.Join(", ", unknown)}");
    }

    private static Kernel GuardedChatKernel()
    {
        var kernel = new Kernel();
        kernel.Data["profile"] = KernelProfile.Chat;
        kernel.Data["isOwner"] = true;
        kernel.FunctionInvocationFilters.Add(
            new AutonomousToolGuardFilter(NullLogger<AutonomousToolGuardFilter>.Instance));
        return kernel;
    }

    private static Task<FunctionResult> InvokeAsync(Kernel kernel, string functionName) =>
        kernel.InvokeAsync(KernelFunctionFactory.CreateFromMethod(() => "ok", functionName: functionName));

    // --- Finding B: per-request kernel isolation ---

    [Fact]
    public void KernelFactory_ReturnsFreshInstancePerCall()
    {
        var factory = BuildFactory();

        var a = factory.Create(KernelProfile.Chat);
        var b = factory.Create(KernelProfile.Chat);

        a.Should().NotBeSameAs(b, because: "a singleton kernel would reintroduce the cross-request race");
        a.Data.Should().NotBeSameAs(b.Data, because: "each invocation needs its own Kernel.Data carrier");
    }

    [Fact]
    public async Task KernelFactory_DoesNotBleedIdentityAcrossConcurrentInvocations()
    {
        var factory = BuildFactory();

        // Each task stamps its own identity into Kernel.Data, yields to force interleaving,
        // then reads it back. If kernels shared Data (the old singleton bug), reads would bleed.
        var tasks = Enumerable.Range(0, 200).Select(async i =>
        {
            var kernel = factory.Create(KernelProfile.Chat);
            var phone = $"+1555{i:D7}";
            kernel.Data["userPhoneNumber"] = phone;
            await Task.Yield();
            return (string)kernel.Data["userPhoneNumber"]! == phone;
        });

        var results = await Task.WhenAll(tasks);
        results.Should().OnlyContain(observedOwnIdentity => observedOwnIdentity,
            because: "each invocation must observe its own identity — no Kernel.Data bleed");
    }

    // --- A4: Cooldown enforcement tests ---

    [Fact]
    public async Task StandingRuleProcessor_SuppressesNotification_WhenCooldownActive()
    {
        // Arrange: rule notified 1 hour ago with a 7-day cooldown — window has NOT elapsed.
        var rule = BuildRule(lastNotifiedAt: DateTimeOffset.UtcNow.AddHours(-1), cooldownDays: 7);
        var user = BuildUser(rule);

        // Model says "notify" — the processor must suppress due to cooldown.
        var mockAgent = BuildMockAgent(rule, user, shouldNotify: true, message: "Price dropped!");
        var smsService = new SpySmsService();
        var (mockStore, mockScheduler) = BuildStoreMocks(rule);
        var mockUserService = BuildUserServiceMock(rule, user);

        var processor = BuildProcessor(mockStore, mockScheduler, mockUserService, smsService, mockAgent);

        await processor.Run(SerializeQueueMessage(rule));

        smsService.SendCount.Should().Be(0, because: "cooldown is active — SMS must be suppressed");
        mockStore.Verify(
            s => s.MarkNotifiedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()),
            Times.Never);
    }

    [Fact]
    public async Task StandingRuleProcessor_SendsToFixedOwnerNumber_WhenCooldownElapsed()
    {
        // Arrange: rule last notified 2 days ago with a 1-day cooldown — window HAS elapsed.
        var rule = BuildRule(lastNotifiedAt: DateTimeOffset.UtcNow.AddDays(-2), cooldownDays: 1);
        var user = BuildUser(rule);

        var mockAgent = BuildMockAgent(rule, user, shouldNotify: true, message: "Price dropped!");
        var smsService = new SpySmsService();
        var (mockStore, mockScheduler) = BuildStoreMocks(rule, expectMarkNotified: true);
        var mockUserService = BuildUserServiceMock(rule, user);

        var processor = BuildProcessor(mockStore, mockScheduler, mockUserService, smsService, mockAgent);

        await processor.Run(SerializeQueueMessage(rule));

        smsService.SendCount.Should().Be(1);
        smsService.LastRecipient.Should().Be(rule.UserPhoneNumber,
            because: "SMS must be sent to the fixed owner number, never to a model-chosen recipient");
        smsService.LastMessage.Should().Be("Price dropped!");
        mockStore.Verify(
            s => s.MarkNotifiedAsync(rule.Id, rule.AgentPhoneNumber, It.IsAny<DateTimeOffset>()),
            Times.Once);
    }

    // --- Helpers ---

    private static StandingRule BuildRule(DateTimeOffset? lastNotifiedAt, int cooldownDays) => new()
    {
        Id = Guid.NewGuid(),
        UserPhoneNumber = "+15559876543",
        AgentPhoneNumber = "+15551234567",
        Description = "Notify when price drops",
        EvaluationIntervalHours = 24,
        NotificationCooldownDays = cooldownDays,
        LastNotifiedAt = lastNotifiedAt,
        Status = RuleStatus.Active,
    };

    private static UserProfile BuildUser(StandingRule rule) => new()
    {
        Id = Guid.NewGuid(),
        AgentPhoneNumber = rule.AgentPhoneNumber,
        UserPhoneNumber = rule.UserPhoneNumber,
        Name = "Test User",
        TimeZone = "America/Los_Angeles",
        CreatedAt = DateTimeOffset.UtcNow,
        RegisteredAt = DateTimeOffset.UtcNow,
    };

    private static Agent BuildMockAgent(StandingRule rule, UserProfile user, bool shouldNotify, string message)
    {
        Environment.SetEnvironmentVariable("OWNER_PHONE_NUMBER", "+15551234567");
        var mockAgent = new Mock<Agent>(
            new Mock<IKernelFactory>().Object,
            null!,
            new Mock<IHistoryService>().Object,
            new CorrelationContext(),
            new OwnerService(),
            new Mock<IMediaService>().Object,
            new Mock<IProjectStore>().Object) { CallBase = false };
        mockAgent
            .Setup(a => a.EvaluateStandingRule(rule, user))
            .ReturnsAsync(new StandingRuleEvaluation
            {
                ShouldNotify = shouldNotify,
                Message = message,
                Reasoning = shouldNotify ? $"NOTIFY: {message}" : "NO_ACTION: condition not met",
            });
        return mockAgent.Object;
    }

    private static (Mock<IStandingRuleStore> store, Mock<IStandingRuleScheduler> scheduler)
        BuildStoreMocks(StandingRule rule, bool expectMarkNotified = false)
    {
        var mockStore = new Mock<IStandingRuleStore>();
        mockStore.Setup(s => s.GetAsync(rule.Id, rule.AgentPhoneNumber)).ReturnsAsync(rule);
        mockStore.Setup(s => s.MarkEvaluatedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(true);
        if (expectMarkNotified)
            mockStore.Setup(s => s.MarkNotifiedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()))
                .ReturnsAsync(true);
        mockStore.Setup(s => s.UpdateNextEvalSequenceAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<long>()))
            .Returns(Task.CompletedTask);

        var mockScheduler = new Mock<IStandingRuleScheduler>();
        mockScheduler.Setup(s => s.ScheduleNextAsync(It.IsAny<StandingRule>(), It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(1L);

        return (mockStore, mockScheduler);
    }

    private static Mock<IUserService> BuildUserServiceMock(StandingRule rule, UserProfile user)
    {
        var mock = new Mock<IUserService>();
        mock.Setup(u => u.GetAsync(rule.AgentPhoneNumber, rule.UserPhoneNumber)).ReturnsAsync(user);
        return mock;
    }

    private static FunctionApp.StandingRuleQueueProcessor BuildProcessor(
        Mock<IStandingRuleStore> store,
        Mock<IStandingRuleScheduler> scheduler,
        Mock<IUserService> userService,
        SpySmsService smsService,
        Agent agent)
    {
        var auditLog = new Mock<IAuditLog>();
        auditLog.Setup(a => a.LogAsync(It.IsAny<AuditEntry>())).Returns(Task.CompletedTask);
        var correlation = new CorrelationContext();
        correlation.New();
        return new FunctionApp.StandingRuleQueueProcessor(
            NullLogger<FunctionApp.StandingRuleQueueProcessor>.Instance,
            store.Object,
            scheduler.Object,
            userService.Object,
            smsService,
            auditLog.Object,
            correlation,
            agent);
    }

    private static string SerializeQueueMessage(StandingRule rule) =>
        System.Text.Json.JsonSerializer.Serialize(
            new StandingRuleQueueMessage { RuleId = rule.Id, AgentPhoneNumber = rule.AgentPhoneNumber });

    private sealed class SpySmsService() : SmsService()
    {
        public int SendCount { get; private set; }
        public string? LastRecipient { get; private set; }
        public string? LastMessage { get; private set; }

        public override Task Send(string phoneNumbers, string message)
        {
            SendCount++;
            LastRecipient = phoneNumbers;
            LastMessage = message;
            return Task.CompletedTask;
        }
    }
}
