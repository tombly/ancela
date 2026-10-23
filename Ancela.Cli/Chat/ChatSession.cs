using System.Diagnostics;
using System.Text.Json;
using Ancela.Agent.Services;
using Azure.Messaging.ServiceBus;
using RadLine;
using Spectre.Console;

namespace Ancela.Cli.Chat;

/// <summary>
/// The chat itself: your message goes on the chat-messages queue tagged as from the CLI, and the
/// chat follows it through the audit log by its ID, showing each tool call as it finishes and
/// then the reply. Between messages it shows anything Ancela texted you meanwhile.
/// </summary>
public sealed class ChatSession(
    ChatFeed _feed,
    ServiceBusSender _sender,
    ChatState _state,
    string _agentPhoneNumber,
    string _userPhoneNumber,
    string? _name,
    TimeZoneInfo _timeZone)
{
    // Long enough for a message that reads email and searches the web; after that the reply
    // still shows, before the next prompt, whenever it lands.
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(1);

    // First run: show the last day's texts rather than everything ever sent.
    private static readonly TimeSpan FirstLook = TimeSpan.FromDays(1);
    private const int AwayLimit = 10;

    private static readonly string[] Commands = ["/help", "/history", "/clear", "/exit"];

    private readonly List<(Guid Id, string Text)> _unanswered = [];

    public async Task<int> RunAsync(string? message, CancellationToken ct)
    {
        if (message is not null)
        {
            await SendAndWaitAsync(message, ct);
            return 0;
        }

        var editor = Editor();
        if (editor is not null)
            ClearScreen();
        Header();
        await ShowTextedMeanwhileAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            string? input;
            if (editor is null)
                input = Console.ReadLine();
            else
            {
                DrawInputArea();
                input = await editor.ReadLine(ct);
                ClearInputArea(input);
            }
            if (input is null)
                break; // Ctrl+D, or end of piped input

            var text = input.Trim();
            if (text.Length == 0)
                continue;
            editor?.History.Add(input);
            if (editor is not null)
                Sent(text);

            if (text.StartsWith('/'))
            {
                if (!await RunCommandAsync(text, ct))
                    break;
            }
            else
                await SendAndWaitAsync(text, ct);

            await ShowLateRepliesAsync(ct);
            await ShowTextedMeanwhileAsync(ct);
            AnsiConsole.WriteLine();
        }

        return 0;
    }

    private async Task SendAndWaitAsync(string text, CancellationToken ct)
    {
        var message = new ChatQueueMessage
        {
            Content = text,
            UserPhoneNumber = _userPhoneNumber,
            AgentPhoneNumber = _agentPhoneNumber,
            Channel = ChatChannel.Cli,
        };
        await _sender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.SerializeToUtf8Bytes(message)), ct);

        var shown = new HashSet<string>();
        FeedEntry? reply = null;
        var stoppedWaiting = false;
        var clock = Stopwatch.StartNew();

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Star)
            .SpinnerStyle(new Style(Color.MediumPurple1))
            .StartAsync("Thinking…", async status =>
            {
                while (reply is null && clock.Elapsed < GiveUpAfter)
                {
                    foreach (var entry in await _feed.ForMessageAsync(message.Id, ct))
                    {
                        if (!shown.Add(entry.Id))
                            continue;
                        if (entry.Category == OutboundLog.Category)
                            reply = entry;
                        else if (entry.Category == "tool")
                            ToolLine(entry);
                    }
                    if (reply is not null || EscapePressed())
                    {
                        stoppedWaiting = reply is null;
                        break;
                    }

                    status.Status($"Thinking… [grey]({(int)clock.Elapsed.TotalSeconds}s · esc to stop waiting)[/]");
                    await Task.Delay(PollEvery, ct);
                }
            });

        if (reply is not null)
        {
            Reply(reply.Result);
            return;
        }

        _unanswered.Add((message.Id, text));
        AnsiConsole.MarkupLine(stoppedWaiting
            ? "  [grey]Stopped waiting. Ancela is still on it; the reply will show here when it lands.[/]"
            : $"  [yellow]No reply after {GiveUpAfter.TotalMinutes:0} minutes.[/] [grey]It will show here if it lands. If not, `ancela list audit` shows how far it got.[/]");
    }

    private static bool EscapePressed()
    {
        if (Console.IsInputRedirected)
            return false;
        // Reads (and drops) whatever was typed while waiting; only Esc means anything here.
        while (Console.KeyAvailable)
            if (Console.ReadKey(intercept: true).Key == ConsoleKey.Escape)
                return true;
        return false;
    }

    private async Task ShowLateRepliesAsync(CancellationToken ct)
    {
        foreach (var (id, text) in _unanswered.ToList())
        {
            var reply = (await _feed.ForMessageAsync(id, ct)).FirstOrDefault(e => e.Category == OutboundLog.Category);
            if (reply is null)
                continue;
            _unanswered.Remove((id, text));
            AnsiConsole.MarkupLineInterpolated($"  [grey]↳ Reply to “{ChatFormat.Line(text, 60)}”[/]");
            Reply(reply.Result);
        }
    }

    private async Task ShowTextedMeanwhileAsync(CancellationToken ct)
    {
        var since = _state.LastSeenSmsTs > 0
            ? _state.LastSeenSmsTs
            : DateTimeOffset.UtcNow.Subtract(FirstLook).ToUnixTimeSeconds();
        var texted = await _feed.SentBySmsSinceAsync(since, AwayLimit, ct);
        if (texted.Count == 0)
            return;

        AnsiConsole.MarkupLine("  [grey]↳ Texted to you meanwhile[/]");
        foreach (var entry in texted)
        {
            var heading = $"[grey]{ChatFormat.When(entry.Timestamp, _timeZone, DateTimeOffset.UtcNow)}[/]  [springgreen2]{Markup.Escape(ChatFormat.Kind(entry.Function))}[/]";
            Bullet("springgreen2", new Rows(new Markup(heading), new Text(entry.Result ?? string.Empty)));
        }

        _state.LastSeenSmsTs = texted.Max(e => e.Ts);
        _state.Save();
    }

    private async Task<bool> RunCommandAsync(string command, CancellationToken ct)
    {
        switch (command.Split(' ', 2)[0].ToLowerInvariant())
        {
            case "/exit" or "/quit":
                return false;

            case "/clear":
                ClearScreen();
                Header();
                return true;

            case "/history":
                var history = await _feed.HistoryAsync(ct);
                if (history.Count == 0)
                    AnsiConsole.MarkupLine("  [grey]No history yet.[/]");
                foreach (var entry in history)
                {
                    var when = ChatFormat.When(entry.Timestamp, _timeZone, DateTimeOffset.UtcNow);
                    if (entry.MessageType == MessageType.User)
                        AnsiConsole.MarkupLineInterpolated($"[grey]> {entry.Content}  · {when}[/]");
                    else
                        Reply(Agent.Agent.WithoutHeldNote(entry.Content));
                }
                return true;

            case "/help":
                AnsiConsole.Write(new Grid()
                    .AddColumn(new GridColumn().PadRight(3))
                    .AddColumn()
                    .AddRow("[mediumpurple1]/history[/]", "The conversation so far (Ancela keeps the last 20 messages)")
                    .AddRow("[mediumpurple1]/clear[/]", "Clear the screen")
                    .AddRow("[mediumpurple1]/exit[/]", "Leave (or Ctrl+D)")
                    .AddRow("[grey]Enter[/]", "Send")
                    .AddRow("[grey]Shift+Enter, Option+Enter[/]", "New line")
                    .AddRow("[grey]Up, Down[/]", "Earlier messages you typed")
                    .AddRow("[grey]Esc[/]", "Stop waiting for a reply (it still shows when it lands)"));
                return true;

            default:
                AnsiConsole.MarkupLineInterpolated($"  [grey]Unknown command {command}. /help lists them.[/]");
                return true;
        }
    }

    private const string Hint = "/help for commands · ↑↓ earlier messages · ⇧↵ new line · ctrl+d to leave";

    // Clears the window (not the scrollback, so what was there before is still above) and starts
    // at the top, as Claude Code does.
    private static void ClearScreen() => AnsiConsole.Console.WriteAnsi("\u001b[2J\u001b[H");

    /// <summary>
    /// Draws the input area below the conversation (a rule, the input line, a rule and the hint)
    /// and leaves the cursor on the input line for RadLine.
    /// </summary>
    private static void DrawInputArea()
    {
        AnsiConsole.Write(new Rule().RuleStyle(new Style(Color.Grey37)));
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule().RuleStyle(new Style(Color.Grey37)));
        AnsiConsole.Markup($"  [grey]{Markup.Escape(ChatFormat.Line(Hint, Math.Max(10, AnsiConsole.Profile.Width - 3)))}[/]");
        AnsiConsole.Console.WriteAnsi("\u001b[2A\r");
    }

    /// <summary>
    /// Removes the input area once you've pressed Enter, so what you sent can be shown as part of
    /// the conversation. RadLine gives every input line exactly one row (it scrolls a long line
    /// sideways rather than wrapping it) and leaves the cursor on the row below the last one, so the
    /// top rule is one row above the input's first line.
    /// </summary>
    private static void ClearInputArea(string? input)
    {
        var lines = input is null ? 1 : input.Split('\n').Length;
        AnsiConsole.Console.WriteAnsi($"\u001b[{lines + 1}A\r\u001b[J");
    }

    // What you sent, in full and wrapped, as Claude Code shows it in the conversation.
    private static void Sent(string text)
    {
        AnsiConsole.Write(new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(1))
            .AddColumn()
            .AddRow(new Markup("[grey]>[/]"), new Text(text)));
        AnsiConsole.WriteLine();
    }

    private void Header()
    {
        var who = _name is null ? "you" : Markup.Escape(_name);
        AnsiConsole.Write(new Panel(new Markup($"Chatting as {who}. Replies come here, not by text."))
        {
            Header = new PanelHeader("[mediumpurple1]✻[/] [bold white]Ancela[/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Grey37),
            Padding = new Padding(1, 0),
        });
        AnsiConsole.WriteLine();
    }

    private static void ToolLine(FeedEntry entry)
    {
        var call = $"[bold]{Markup.Escape(entry.Function)}[/][grey]({Markup.Escape(ChatFormat.Arguments(entry.Arguments))})[/]";
        var outcome = entry.Success
            ? $"[grey]⎿  {Markup.Escape(ChatFormat.Result(entry.Result))}[/]"
            : $"[red]⎿  {Markup.Escape(ChatFormat.Line(entry.Error ?? "Failed", 100))}[/]";
        Bullet(entry.Success ? "deepskyblue1" : "red", new Rows(new Markup(call), new Markup(outcome)));
    }

    private static void Reply(string? text) =>
        Bullet("white", string.IsNullOrWhiteSpace(text)
            ? new Markup("[grey](Ancela didn't reply. Is your number registered?)[/]")
            : new Text(text.Trim()));

    // A coloured dot with the content beside it, so wrapped lines stay indented under the text.
    private static void Bullet(string color, Spectre.Console.Rendering.IRenderable content)
    {
        AnsiConsole.Write(new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(1))
            .AddColumn()
            .AddRow(new Markup($"[{color}]●[/]"), content));
    }

    private static LineEditor? Editor()
    {
        if (Console.IsInputRedirected || !LineEditor.IsSupported(AnsiConsole.Console))
            return null;

        var editor = new LineEditor(AnsiConsole.Console)
        {
            MultiLine = true,
            Prompt = new LineEditorPrompt("[mediumpurple1]>[/]", "[grey]·[/]"),
            Completion = new CommandCompletion(),
        };
        // As in Claude Code: Up and Down go through what you typed, not between lines, and
        // Option+Enter adds a line for terminals that send Shift+Enter as plain Enter.
        editor.KeyBindings.Remove(ConsoleKey.UpArrow, null);
        editor.KeyBindings.Remove(ConsoleKey.DownArrow, null);
        editor.KeyBindings.Add<PreviousHistoryCommand>(ConsoleKey.UpArrow);
        editor.KeyBindings.Add<NextHistoryCommand>(ConsoleKey.DownArrow);
        editor.KeyBindings.Add<NewLineCommand>(ConsoleKey.Enter, ConsoleModifiers.Alt);
        editor.KeyBindings.Add<LeaveCommand>(ConsoleKey.D, ConsoleModifiers.Control);
        return editor;
    }

    private sealed class CommandCompletion : ITextCompletion
    {
        public IEnumerable<string>? GetCompletions(string prefix, string word, string suffix) =>
            prefix.Length == 0 && word.StartsWith('/')
                ? Commands.Where(c => c.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                : null;
    }

    /// <summary>Ctrl+D on an empty line leaves, as in a shell.</summary>
    private sealed class LeaveCommand : LineEditorCommand
    {
        public override void Execute(LineEditorContext context)
        {
            if (context.Buffer.Length == 0)
                context.Submit(SubmitAction.Cancel);
        }
    }
}
