using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using FluentAssertions;
using Microsoft.SemanticKernel;
using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// save_email_to_onedrive asks before downloading an email's linked images, as
/// send_email_to_remarkable does: downloading them tells the sender the email was opened.
/// </summary>
public class SaveEmailTests
{
    private readonly Mock<IGraphClient> _graph = new();

    private GraphPlugin Plugin => new(_graph.Object);

    [Fact]
    public async Task SaveEmail_WithLinkedImages_SavesNothingUntilTheUserAnswers()
    {
        _graph.Setup(g => g.GetLinkedImageCountAsync("email-1")).ReturnsAsync(1);

        var saved = await Plugin.SaveEmailToOneDriveAsync(new Kernel(), "email-1");

        saved.Saved.Should().BeFalse();
        saved.Note.Should().StartWith("Nothing was saved yet. The email links to 1 image on the sender's servers");
        _graph.Verify(g => g.SaveEmailAsPdfAsync(It.IsAny<string>(), It.IsAny<TimeZoneInfo>(), It.IsAny<bool>()), Times.Never);
    }

    [Theory]
    [InlineData(null, 0, false)]   // nothing linked, nothing to ask
    [InlineData(true, 5, true)]
    [InlineData(false, 5, false)]
    public async Task SaveEmail_SavesWithTheAnswer_OrWhenThereIsNothingToAsk(bool? answer, int linked, bool expected)
    {
        _graph.Setup(g => g.GetLinkedImageCountAsync("email-1")).ReturnsAsync(linked);
        _graph
            .Setup(g => g.SaveEmailAsPdfAsync("email-1", It.IsAny<TimeZoneInfo>(), It.IsAny<bool>()))
            .ReturnsAsync(new SavedEmailModel { Saved = true, FileName = "2026-10-22 2100 Fall catalog.pdf", LinkedImages = linked });

        var saved = await Plugin.SaveEmailToOneDriveAsync(new Kernel(), "email-1", answer);

        saved.Saved.Should().BeTrue();
        _graph.Verify(g => g.SaveEmailAsPdfAsync("email-1", It.IsAny<TimeZoneInfo>(), expected), Times.Once);
    }
}
