using System.Text.Json;
using Ancela.Agent.SemanticKernel.Plugins.ReminderPlugin;
using Ancela.Agent.SemanticKernel.Plugins.ReminderPlugin.Models;
using Ancela.Agent.Services;
using Ancela.Cli.Chat;
using Ancela.FunctionApp;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// `ancela chat` puts its messages on the chat queue tagged as from the CLI and reads the reply
/// back from the audit log by the message's ID, along with what Ancela texted meanwhile.
/// </summary>
public class CliChatTests
{
    private const string AgentPhoneNumber = "+15550000000";
    private const string OwnerPhoneNumber = "+15551234567";

    private readonly List<AuditEntry> _audit = [];
    private readonly Mock<IAuditLog> _auditLog = new();
    private readonly CorrelationContext _correlation = new();
    private readonly SpySmsService _sms;

    public CliChatTests()
    {
        Environment.SetEnvironmentVariable("TWILIO_PHONE_NUMBER", "+10000000000");
        Environment.SetEnvironmentVariable("TWILIO_ACCOUNT_SID", "ACXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX");
        Environment.SetEnvironmentVariable("TWILIO_AUTH_TOKEN", "test-token");
        Environment.SetEnvironmentVariable("OWNER_PHONE_NUMBER", OwnerPhoneNumber);
        _auditLog.Setup(a => a.LogAsync(It.IsAny<AuditEntry>())).Callback<AuditEntry>(_audit.Add).Returns(Task.CompletedTask);
        _sms = new SpySmsService();
    }

    [Fact]
    public void QueueMessage_QueuedBeforeChannels_IsAnSmsWithItsOwnId()
    {
        var old = JsonSerializer.Deserialize<ChatQueueMessage>(
            """{"Content":"hi","UserPhoneNumber":"+15551234567","AgentPhoneNumber":"+15550000000","Media":[]}""")!;

        old.Channel.Should().Be(ChatChannel.Sms);
        old.Id.Should().NotBeEmpty();
    }

    [Fact]
    public void QueueMessage_RoundTripsItsChannelAndId_WithTheChannelAsAName()
    {
        var message = new ChatQueueMessage { Content = "hi", Channel = ChatChannel.Cli };

        var json = JsonSerializer.Serialize(message);
        var back = JsonSerializer.Deserialize<ChatQueueMessage>(json)!;

        json.Should().Contain("\"Channel\":\"Cli\"");
        back.Channel.Should().Be(ChatChannel.Cli);
        back.Id.Should().Be(message.Id);
    }

    [Fact]
    public async Task CliMessage_ReplyGoesToTheAuditLog_UnderTheMessagesId_AndNotByText()
    {
        var message = Message(ChatChannel.Cli);
        Guid? correlationWhileHandling = null;
        var processor = Processor(reply: "You have 3 to-dos.", onHandle: () => correlationWhileHandling = _correlation.Current);

        await processor.Run(JsonSerializer.Serialize(message));

        _sms.Sent.Should().BeEmpty(because: "the CLI is for when texts can't reach the phone; one would arrive later as a duplicate");
        correlationWhileHandling.Should().Be(message.Id, because: "the CLI follows the message's tool calls by its ID");
        var reply = _audit.Should().ContainSingle(e => e.Category == OutboundLog.Category).Subject;
        reply.CorrelationId.Should().Be(message.Id);
        reply.Plugin.Should().Be("cli");
        reply.Function.Should().Be("reply");
        reply.Result.Should().Be("You have 3 to-dos.");
        reply.UserPhoneNumber.Should().Be(OwnerPhoneNumber);
    }

    [Fact]
    public async Task SmsMessage_ReplyIsTexted_AndNotRecordedAgain()
    {
        await Processor(reply: "You have 3 to-dos.").Run(JsonSerializer.Serialize(Message(ChatChannel.Sms)));

        _sms.Sent.Should().ContainSingle().Which.Should().Be((OwnerPhoneNumber, "You have 3 to-dos."));
        _audit.Should().NotContain(e => e.Category == OutboundLog.Category, because: "history already holds SMS replies");
    }

    [Fact]
    public async Task CliMessage_WithNoReply_StillTellsTheCliToStopWaiting()
    {
        var message = Message(ChatChannel.Cli);

        await Processor(reply: null).Run(JsonSerializer.Serialize(message));

        _audit.Should().ContainSingle(e => e.Category == OutboundLog.Category && e.CorrelationId == message.Id)
            .Which.Result.Should().BeEmpty();
        _sms.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task CliMessage_OutOfCredit_TheExplanationReachesTheCli()
    {
        var message = Message(ChatChannel.Cli);

        await Processor(reply: null, throws: new ModelOutOfFundsException()).Run(JsonSerializer.Serialize(message));

        _audit.Should().ContainSingle(e => e.Category == OutboundLog.Category && e.CorrelationId == message.Id)
            .Which.Result.Should().Be(OutOfFundsMessages.ForUser(isOwner: true));
        _sms.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Reminder_IsTexted_AndRecorded_SoTheCliCanShowIt()
    {
        var reminder = new Reminder
        {
            Id = Guid.NewGuid(),
            UserPhoneNumber = OwnerPhoneNumber,
            AgentPhoneNumber = AgentPhoneNumber,
            DueAt = DateTimeOffset.UtcNow,
            Message = "Renew the car tags",
            Status = ReminderStatus.Scheduled,
        };
        var store = new Mock<IReminderStore>();
        store.Setup(s => s.GetAsync(reminder.Id, AgentPhoneNumber)).ReturnsAsync(reminder);
        store.Setup(s => s.MarkSentAsync(reminder.Id, AgentPhoneNumber, It.IsAny<DateTimeOffset>())).ReturnsAsync(true);
        var processor = new ReminderQueueProcessor(
            NullLogger<ReminderQueueProcessor>.Instance, store.Object, _sms, new OutboundLog(_auditLog.Object, _correlation));

        await processor.Run(JsonSerializer.Serialize(new ReminderQueueMessage { ReminderId = reminder.Id, AgentPhoneNumber = AgentPhoneNumber }));

        _sms.Sent.Should().ContainSingle().Which.Should().Be((OwnerPhoneNumber, "Renew the car tags"));
        var sent = _audit.Should().ContainSingle(e => e.Category == OutboundLog.Category).Subject;
        sent.Plugin.Should().Be("sms");
        sent.Function.Should().Be("reminder");
        sent.Result.Should().Be("Renew the car tags");
    }

    [Fact]
    public void Correlation_SetByTheQueue_IsKeptByWhatHandlesTheMessage()
    {
        var id = Guid.NewGuid();
        _correlation.Use(id);

        _correlation.New();

        _correlation.Current.Should().Be(id);
    }

    [Fact]
    public async Task Correlation_WithoutOneSet_StartsANewOnePerMessage()
    {
        var first = await StartedInItsOwnFlow();
        var second = await StartedInItsOwnFlow();

        first.Should().NotBeEmpty();
        second.Should().NotBeEmpty().And.NotBe(first);
        _correlation.Current.Should().BeEmpty(because: "a message's correlation ends with it");

        async Task<Guid> StartedInItsOwnFlow()
        {
            await Task.Yield();
            _correlation.New();
            return _correlation.Current;
        }
    }

    [Fact]
    public void History_ShownWithoutTheNoteForTheModel()
    {
        var stored = "I'd reply to bob@example.com: \"Yes.\" Confirm?\n\n(Note for you, not shown to the user: send_email was switched off for this message.)";

        Agent.WithoutHeldNote(stored).Should().Be("I'd reply to bob@example.com: \"Yes.\" Confirm?");
        Agent.WithoutHeldNote("Plain reply.").Should().Be("Plain reply.");
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("""{"query":"mill creek weather","maxResults":"5"}""", "query: \"mill creek weather\", maxResults: 5")]
    [InlineData("""{"includeLinks":"False"}""", "includeLinks: False")]
    public void ToolLine_Arguments(string? json, string expected) =>
        ChatFormat.Arguments(json).Should().Be(expected);

    [Theory]
    [InlineData(null, "Done")]
    [InlineData("""[{"id":1},{"id":2},{"id":3}]""", "3 results")]
    [InlineData("[]", "Nothing found")]
    [InlineData("No data: get_todos worked and found nothing for this request. This is not an error.", "Nothing found")]
    [InlineData("""{"truncated":true,"originalLength":9000,"preview":"..."}""", "Large result")]
    [InlineData("Email sent.", "Email sent.")]
    public void ToolLine_Result(string? result, string expected) =>
        ChatFormat.Result(result).Should().Be(expected);

    [Fact]
    public void When_IsShortForToday_AndCarriesTheDayOtherwise()
    {
        var pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        var now = new DateTimeOffset(2026, 10, 23, 20, 0, 0, TimeSpan.Zero); // 13:00 Pacific, a Friday

        ChatFormat.When(new DateTimeOffset(2026, 10, 23, 14, 30, 0, TimeSpan.Zero), pacific, now).Should().Be("07:30");
        ChatFormat.When(new DateTimeOffset(2026, 10, 20, 14, 30, 0, TimeSpan.Zero), pacific, now).Should().Be("Tue 07:30");
        ChatFormat.When(new DateTimeOffset(2026, 10, 1, 14, 30, 0, TimeSpan.Zero), pacific, now).Should().Be("Oct 1 07:30");
    }

    private static ChatQueueMessage Message(ChatChannel channel) => new()
    {
        Content = "what's on my list?",
        UserPhoneNumber = OwnerPhoneNumber,
        AgentPhoneNumber = AgentPhoneNumber,
        Channel = channel,
    };

    private ChatQueueProcessor Processor(string? reply, Action? onHandle = null, Exception? throws = null)
    {
        var interceptor = new Mock<ChatInterceptor>(null!, null!, null!, null!, null!, null!, null!, null!);
        var setup = interceptor.Setup(i => i.HandleMessage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Media[]>()));
        if (throws is not null)
            setup.ThrowsAsync(throws);
        else
            setup.Callback(() => onHandle?.Invoke()).ReturnsAsync(reply);

        return new ChatQueueProcessor(
            NullLogger<ChatQueueProcessor>.Instance,
            interceptor.Object,
            _sms,
            new OwnerService(),
            new OutboundLog(_auditLog.Object, _correlation),
            _correlation);
    }

    private sealed class SpySmsService() : SmsService()
    {
        public List<(string To, string Message)> Sent { get; } = [];

        public override Task Send(string phoneNumbers, string message)
        {
            Sent.Add((phoneNumbers, message));
            return Task.CompletedTask;
        }
    }
}
