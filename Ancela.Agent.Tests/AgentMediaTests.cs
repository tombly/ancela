using Ancela.Agent.SemanticKernel;
using Ancela.Agent.Services;
using FluentAssertions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// Orchestration tests for inbound media handling in <see cref="Agent.Chat"/>. The vision pass and
/// main turn are both served by a mocked <see cref="IChatCompletionService"/>, so these run without
/// a live OpenAI key. They verify the describe-and-store contract: history holds the description
/// text (not raw bytes), media-only messages still produce a turn, and unsupported types are noted.
/// </summary>
public class AgentMediaTests
{
    private const string AgentPhone = "+15551234567";
    private const string UserPhone = "+15559876543";
    private const string TwilioUrl = "https://api.twilio.com/2010-04-01/Accounts/AC/Messages/MM/Media/ME";

    public AgentMediaTests()
    {
        // Treat the test user as the owner so no owner-only filtering applies.
        Environment.SetEnvironmentVariable("OWNER_PHONE_NUMBER", UserPhone);
    }

    [Fact]
    public async Task Chat_ImageOnly_ProducesTurn_AndStoresDescriptionNotBytes()
    {
        var media = new Mock<IMediaService>();
        media.Setup(m => m.IsSupportedImage("image/jpeg")).Returns(true);
        media.Setup(m => m.FetchAsync(TwilioUrl, "image/jpeg"))
            .ReturnsAsync(new MediaItem([1, 2, 3], "image/jpeg"));
        media.Setup(m => m.PersistAsync(It.IsAny<MediaItem>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new Uri("https://acct.blob.core.windows.net/media/x.jpg"));

        var (history, storedUser) = HistoryCapturing();
        var chat = SequencedChat("a red bicycle leaning on a brick wall", "Nice bike!");
        var agent = BuildAgent(chat, history.Object, media.Object);

        // Media-only: blank text, one image.
        var reply = await agent.Chat("", UserPhone, AgentPhone, TestUser(), [new Media(TwilioUrl, "image/jpeg")]);

        reply.Should().Be("Nice bike!");
        storedUser.Value.Should().Contain("a red bicycle leaning on a brick wall");
        storedUser.Value.Should().Contain("untrusted");
        media.Verify(m => m.FetchAsync(TwilioUrl, "image/jpeg"), Times.Once);
        media.Verify(m => m.PersistAsync(It.IsAny<MediaItem>(), AgentPhone, UserPhone), Times.Once);
    }

    [Fact]
    public async Task Chat_UnsupportedAttachment_IsNoted_AndNotFetched()
    {
        var media = new Mock<IMediaService>();
        media.Setup(m => m.IsSupportedImage(It.IsAny<string>())).Returns(false);

        var (history, storedUser) = HistoryCapturing();
        var chat = SequencedChat("Sorry, I can only view images.");
        var agent = BuildAgent(chat, history.Object, media.Object);

        var reply = await agent.Chat("look at this", UserPhone, AgentPhone, TestUser(),
            [new Media("https://api.twilio.com/.../Media/ME", "application/pdf")]);

        reply.Should().Be("Sorry, I can only view images.");
        storedUser.Value.Should().Contain("look at this");
        storedUser.Value.Should().Contain("only view images");
        media.Verify(m => m.FetchAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Chat_FetchFails_NotesRetrievalFailure_StillReplies()
    {
        var media = new Mock<IMediaService>();
        media.Setup(m => m.IsSupportedImage("image/png")).Returns(true);
        media.Setup(m => m.FetchAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync((MediaItem?)null);

        var (history, storedUser) = HistoryCapturing();
        var chat = SequencedChat("Hmm, I couldn't open that image.");
        var agent = BuildAgent(chat, history.Object, media.Object);

        var reply = await agent.Chat("", UserPhone, AgentPhone, TestUser(), [new Media(TwilioUrl, "image/png")]);

        reply.Should().Be("Hmm, I couldn't open that image.");
        storedUser.Value.Should().Contain("could not be retrieved");
        media.Verify(m => m.PersistAsync(It.IsAny<MediaItem>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    private static Agent BuildAgent(Mock<IChatCompletionService> chat, IHistoryService history, IMediaService media)
    {
        var kernelFactory = new Mock<IKernelFactory>();
        kernelFactory.Setup(f => f.Create(It.IsAny<KernelProfile>())).Returns(() => new Kernel());

        return new Agent(kernelFactory.Object, chat.Object, history, new CorrelationContext(), new OwnerService(), media);
    }

    // Captures the content stored for the User history entry (the description-augmented message).
    private static (Mock<IHistoryService>, StrongBox<string>) HistoryCapturing()
    {
        var stored = new StrongBox<string>(string.Empty);
        var history = new Mock<IHistoryService>();
        history.Setup(h => h.GetHistoryAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync([]);
        history
            .Setup(h => h.CreateHistoryEntryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), MessageType.User))
            .Callback<string, string, string, MessageType>((_, _, content, _) => stored.Value = content)
            .Returns(Task.CompletedTask);
        history
            .Setup(h => h.CreateHistoryEntryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), MessageType.Agent))
            .Returns(Task.CompletedTask);
        return (history, stored);
    }

    // Returns each provided text as the result of successive GetChatMessageContents calls
    // (call 1 = vision pass description, call 2 = main turn reply).
    private static Mock<IChatCompletionService> SequencedChat(params string[] replies)
    {
        var chat = new Mock<IChatCompletionService>();
        var sequence = chat.SetupSequence(c => c.GetChatMessageContentsAsync(
            It.IsAny<ChatHistory>(), It.IsAny<PromptExecutionSettings>(), It.IsAny<Kernel>(), It.IsAny<CancellationToken>()));
        foreach (var reply in replies)
            sequence = sequence.ReturnsAsync([new ChatMessageContent(AuthorRole.Assistant, reply)]);
        return chat;
    }

    private static UserProfile TestUser() => new()
    {
        Id = Guid.NewGuid(),
        AgentPhoneNumber = AgentPhone,
        UserPhoneNumber = UserPhone,
        Name = "Test User",
        TimeZone = "America/Los_Angeles",
        Location = "San Francisco, CA",
        CreatedAt = DateTimeOffset.UtcNow,
        RegisteredAt = DateTimeOffset.UtcNow,
    };

    // Minimal mutable holder so the history callback can publish the captured content.
    private sealed class StrongBox<T>(T value)
    {
        public T Value { get; set; } = value;
    }
}
