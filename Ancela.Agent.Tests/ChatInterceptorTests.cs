using Ancela.Agent.SemanticKernel;
using Ancela.Agent.SemanticKernel.Plugins.ProjectsPlugin;
using Ancela.Agent.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// Tests the owner-invite access gate in ChatInterceptor: only the owner may invite/revoke,
/// uninvited senders can't self-register, and invited (pending) users are onboarded.
/// These paths don't reach the model, so no OpenAI key is needed.
/// </summary>
public class ChatInterceptorTests
{
    private const string AgentPhoneNumber = "+15551234567";
    private const string OwnerPhoneNumber = "+15550000001";
    private const string GuestPhoneNumber = "+15559876543";

    private readonly Mock<IUserService> _userService = new();
    private readonly Mock<IAuditLog> _auditLog = new();
    private readonly Mock<SmsService> _smsService;
    private readonly Mock<Agent> _agent;
    private readonly ChatInterceptor _interceptor;

    public ChatInterceptorTests()
    {
        Environment.SetEnvironmentVariable("TWILIO_PHONE_NUMBER", "+10000000000");
        Environment.SetEnvironmentVariable("TWILIO_ACCOUNT_SID", "ACXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX");
        Environment.SetEnvironmentVariable("TWILIO_AUTH_TOKEN", "test-token");
        Environment.SetEnvironmentVariable("OWNER_PHONE_NUMBER", OwnerPhoneNumber);

        _auditLog.Setup(a => a.LogAsync(It.IsAny<AuditEntry>())).Returns(Task.CompletedTask);
        _smsService = new Mock<SmsService>();
        _smsService.Setup(s => s.Send(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);

        _agent = new Mock<Agent>(
            new Mock<IKernelFactory>().Object,
            null!,
            new Mock<IHistoryService>().Object,
            new CorrelationContext(),
            new OwnerService(),
            new Mock<IMediaService>().Object,
            new Mock<IProjectStore>().Object) { CallBase = false };
        _agent.Setup(a => a.Onboard(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Media[]>()))
            .ReturnsAsync("ONBOARDING");

        _interceptor = new ChatInterceptor(
            NullLogger<ChatInterceptor>.Instance,
            _userService.Object,
            _auditLog.Object,
            new CorrelationContext(),
            new OwnerService(),
            _smsService.Object,
            _agent.Object);
    }

    private UserProfile Pending(string phone) => new()
    {
        Id = Guid.NewGuid(),
        AgentPhoneNumber = AgentPhoneNumber,
        UserPhoneNumber = phone,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private UserProfile Registered(string phone) => new()
    {
        Id = Guid.NewGuid(),
        AgentPhoneNumber = AgentPhoneNumber,
        UserPhoneNumber = phone,
        Name = "Someone",
        TimeZone = "America/Los_Angeles",
        Location = "San Francisco, CA",
        CreatedAt = DateTimeOffset.UtcNow,
        RegisteredAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Invite_FromOwner_CreatesPendingProfileAndNotifiesInvitee()
    {
        var reply = await _interceptor.HandleMessage("invite +1 555 987 6543", OwnerPhoneNumber, AgentPhoneNumber, []);

        reply.Should().Be($"Invited {GuestPhoneNumber}.");
        _userService.Verify(u => u.CreatePendingAsync(AgentPhoneNumber, GuestPhoneNumber), Times.Once);
        _smsService.Verify(s => s.Send(GuestPhoneNumber, It.Is<string>(m => m.Contains("hello ancela"))), Times.Once);
    }

    [Fact]
    public async Task Invite_FromNonOwner_IsNotAPrivilegedCommand_AndIsDropped()
    {
        // A guest typing "invite ..." must not register anyone. With no account, they're dropped.
        var reply = await _interceptor.HandleMessage("invite +15559876543", GuestPhoneNumber, AgentPhoneNumber, []);

        reply.Should().BeNull();
        _userService.Verify(u => u.CreatePendingAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Invite_WithoutCountryCode_IsRejectedWithGuidance()
    {
        var reply = await _interceptor.HandleMessage("invite 5559876543", OwnerPhoneNumber, AgentPhoneNumber, []);

        reply.Should().Contain("international format");
        _userService.Verify(u => u.CreatePendingAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Invite_ExistingUser_ReportsAlreadyHasAccess()
    {
        _userService.Setup(u => u.GetAsync(AgentPhoneNumber, GuestPhoneNumber)).ReturnsAsync(Registered(GuestPhoneNumber));

        var reply = await _interceptor.HandleMessage("invite +15559876543", OwnerPhoneNumber, AgentPhoneNumber, []);

        reply.Should().Be($"{GuestPhoneNumber} already has access.");
        _userService.Verify(u => u.CreatePendingAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Revoke_FromOwner_DeletesExistingUser()
    {
        _userService.Setup(u => u.GetAsync(AgentPhoneNumber, GuestPhoneNumber)).ReturnsAsync(Registered(GuestPhoneNumber));

        var reply = await _interceptor.HandleMessage("revoke +15559876543", OwnerPhoneNumber, AgentPhoneNumber, []);

        reply.Should().Be($"Revoked access for {GuestPhoneNumber}.");
        _userService.Verify(u => u.DeleteAsync(AgentPhoneNumber, GuestPhoneNumber), Times.Once);
    }

    [Fact]
    public async Task Revoke_NonexistentUser_ReportsNoAccess()
    {
        var reply = await _interceptor.HandleMessage("revoke +15559876543", OwnerPhoneNumber, AgentPhoneNumber, []);

        reply.Should().Be($"{GuestPhoneNumber} doesn't have access.");
        _userService.Verify(u => u.DeleteAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Revoke_OwnSelf_IsRefused()
    {
        var reply = await _interceptor.HandleMessage("revoke +15550000001", OwnerPhoneNumber, AgentPhoneNumber, []);

        reply.Should().Contain("can't revoke your own access");
        _userService.Verify(u => u.DeleteAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task HelloAncela_FromUninvitedGuest_IsSilentlyDropped()
    {
        // GetAsync returns null (no invite). The guest must not be able to self-register.
        var reply = await _interceptor.HandleMessage("hello ancela", GuestPhoneNumber, AgentPhoneNumber, []);

        reply.Should().BeNull();
        _userService.Verify(u => u.CreatePendingAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _agent.Verify(a => a.Onboard(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Media[]>()), Times.Never);
    }

    [Fact]
    public async Task HelloAncela_FromInvitedPendingUser_StartsOnboarding()
    {
        _userService.Setup(u => u.GetAsync(AgentPhoneNumber, GuestPhoneNumber)).ReturnsAsync(Pending(GuestPhoneNumber));

        var reply = await _interceptor.HandleMessage("hello ancela", GuestPhoneNumber, AgentPhoneNumber, []);

        reply.Should().Be("ONBOARDING");
        _agent.Verify(a => a.Onboard("hello ancela", GuestPhoneNumber, AgentPhoneNumber, It.IsAny<Media[]>()), Times.Once);
    }

    [Fact]
    public async Task HelloAncela_FromOwner_SelfRegisters()
    {
        var reply = await _interceptor.HandleMessage("hello ancela", OwnerPhoneNumber, AgentPhoneNumber, []);

        reply.Should().Be("ONBOARDING");
        _userService.Verify(u => u.CreatePendingAsync(AgentPhoneNumber, OwnerPhoneNumber), Times.Once);
        _agent.Verify(a => a.Onboard("hello ancela", OwnerPhoneNumber, AgentPhoneNumber, It.IsAny<Media[]>()), Times.Once);
    }
}
