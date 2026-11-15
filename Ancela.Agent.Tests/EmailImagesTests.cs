using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using FluentAssertions;

namespace Ancela.Agent.Tests;

/// <summary>
/// An email's pictures: where each comes from, which are tracking pixels, and the question asked
/// before linked ones are downloaded. EmailEpubTests covers putting them in the book.
/// </summary>
public class EmailImagesTests
{
    [Fact]
    public void ImageSources_ListsEmbeddedAndLinkedImages_DecodedAndInOrder()
    {
        var html = """
            <img alt="logo" src="https://cdn.example.com/a.png?x=1&amp;y=2">
            <img data-src="https://lazy.example.com/b.png" src='cid:part1%40example.com'>
            <img src=https://cdn.example.com/c.gif width="600" height="200">
            <img src="data:image/png;base64,AAAA">
            """;

        EmailImages.ImageSources(html).Should().Equal(
            "https://cdn.example.com/a.png?x=1&y=2", "cid:part1@example.com", "https://cdn.example.com/c.gif", "data:image/png;base64,AAAA");
    }

    [Theory]
    [InlineData("<img src=\"https://t.example.com/open.gif\" width=\"1\" height=\"1\">")]
    [InlineData("<img width=0 height=0 src=\"https://t.example.com/open.gif\">")]
    [InlineData("<img src=\"https://t.example.com/open.gif\" height=\"2\" width=\"2\" style=\"display:none\">")]
    public void ImageSources_LeavesOutTrackingPixels(string html)
    {
        EmailImages.ImageSources(html).Should().BeEmpty(because: "a pixel-sized image is there to report the open, not to be seen");
    }

    [Fact]
    public void ImageSources_KeepsAnImageWithOnlyOneSmallDimension()
    {
        EmailImages.ImageSources("<img src=\"https://cdn.example.com/rule.png\" width=\"600\" height=\"1\">").Should().HaveCount(1);
    }

    [Fact]
    public void LinkedAndEmbedded_AreToldApartByScheme()
    {
        EmailImages.IsLinked("https://cdn.example.com/a.png").Should().BeTrue();
        EmailImages.IsLinked("http://cdn.example.com/a.png").Should().BeTrue();
        EmailImages.IsLinked("cid:part1").Should().BeFalse();
        EmailImages.IsLinked("data:image/png;base64,AAAA").Should().BeFalse();
        EmailImages.IsEmbedded("CID:part1").Should().BeTrue();
        EmailImages.EmbeddedSource("<part1@example.com>").Should().Be("cid:part1@example.com");
    }

    [Fact]
    public void LinkedImagesQuestion_NamesTheCountAndTheTradeOff()
    {
        var note = EmailImages.LinkedImagesQuestion(1, "Nothing was sent yet.");

        note.Should().StartWith("Nothing was sent yet. The email links to 1 image on the sender's servers")
            .And.Contain("lets the sender see that the email was opened")
            .And.Contain("includeLinkedImages");
    }
}
