using System.Text.Json;

namespace Ancela.Cli.Chat;

/// <summary>
/// Where "while you were away" starts: the _ts of the last SMS-sent message the chat showed,
/// kept in ~/.ancela/chat-state.json. Losing it only means seeing a day's messages again.
/// </summary>
public sealed class ChatState
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ancela", "chat-state.json");

    public long LastSeenSmsTs { get; set; }

    public static ChatState Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<ChatState>(File.ReadAllText(FilePath)) ?? new ChatState()
                : new ChatState();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new ChatState();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only costs a repeat of "while you were away" next time.
        }
    }
}
