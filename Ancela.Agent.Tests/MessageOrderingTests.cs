using System.Text.Json;
using FluentAssertions;

namespace Ancela.Agent.Tests;

/// <summary>
/// Pins that the queue processors handle one message at a time. A message is saved to history
/// only after it is answered, so a text handled alongside another never sees it. On 2026-10-19 a
/// phone sent "Add to do: ... at this URL:" and the link as two texts; they were answered in
/// parallel, with "Send me the URL" and "What would you like me to do with it?".
/// </summary>
public class MessageOrderingTests
{
    [Fact]
    public void ServiceBusTriggers_HandleOneMessageAtATime()
    {
        using var hostJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "Ancela.FunctionApp", "host.json")));

        hostJson.RootElement
            .GetProperty("extensions").GetProperty("serviceBus").GetProperty("maxConcurrentCalls").GetInt32()
            .Should().Be(1, "a message handled alongside another can't see it in history");
    }

    // The source file, not the copy in the test output, so the test reads what gets deployed.
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Ancela.slnx")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("Ancela.slnx not found above the test output directory.");
    }
}
