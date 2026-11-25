using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeAssist.Contracts;
using Spectre.Console;

namespace CodeAssist.Tui;

public static class ConversationView
{
    public static void Welcome(IAnsiConsole console, string version, string model, string directory)
    {
        if (!console.Profile.Capabilities.Interactive)
        {
            console.WriteLine("CodeAssist | Service ready");
            return;
        }

        console.WriteLine();
        var details = new Rows(
            new Text($"CodeAssist v{version}", new Style(decoration: Decoration.Bold)),
            new Text(model, new Style(Color.Grey)),
            new Text(directory, new Style(Color.Grey)));
        if (console.Profile.Width >= 60)
        {
            var header = new Grid().AddColumn(new GridColumn().Width(9)).AddColumn();
            header.AddRow(new Text("  / >_\n < /\n  /___", new Style(Color.Cyan1)), details);
            console.Write(header);
        }
        else console.Write(details);
        console.WriteLine();
    }

    public static Task<string?> ReadAsync(IAnsiConsole console, TextReader input, bool json, CancellationToken cancellation = default) =>
        ReadAsync(console, input, json, "permissions: service", cancellation);

    public static async Task<string?> ReadAsync(IAnsiConsole console, TextReader input, bool json, string permissions, CancellationToken cancellation = default)
    {
        if (!json && console.Profile.Capabilities.Interactive)
        {
            console.Write(new Rule { Style = new Style(Color.Grey) });
            if (console.Profile.Capabilities.Ansi && ReferenceEquals(input, Console.In))
            {
                console.WriteLine();
                console.Write(new Rule { Style = new Style(Color.Grey) });
                console.Write(new Text(permissions, new Style(Color.Yellow)));
                console.WriteLine();
                console.Cursor.Move(CursorDirection.Up, 3);
                try
                {
                    return await ReadComposerAsync(console, cancellation);
                }
                finally
                {
                    console.WriteLine();
                    console.Cursor.Move(CursorDirection.Down, 2);
                    console.WriteLine();
                }
            }
            console.Write(new Text(permissions, new Style(Color.Yellow)));
            console.WriteLine();
            console.Write("> ");
        }
        return await input.ReadLineAsync(cancellation);
    }

    private static async Task<string?> ReadComposerAsync(IAnsiConsole console, CancellationToken cancellation)
    {
        var buffer = new StringBuilder();
        var caret = 0;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var value = buffer.ToString();
            var boundaries = StringInfo.ParseCombiningCharacters(value).Append(value.Length).Distinct().ToArray();
            var index = Array.BinarySearch(boundaries, caret);
            if (index < 0) index = ~index;
            var previous = boundaries[Math.Max(0, index - 1)];
            var next = boundaries[Math.Min(boundaries.Length - 1, index + 1)];
            var available = Math.Max(1, console.Profile.Width - 3);
            var start = index;
            while (start > 0 && value[boundaries[start - 1]..caret].GetCellWidth() < available) start--;
            var end = index;
            while (end + 1 < boundaries.Length && value[boundaries[start]..boundaries[end + 1]].GetCellWidth() <= available) end++;
            var visible = value[boundaries[start]..boundaries[end]];
            console.Write("\r");
            console.Write(new Text("> " + visible + new string(' ', available - visible.GetCellWidth())));
            console.Write("\r");
            console.Cursor.Move(CursorDirection.Right, 2 + value[boundaries[start]..caret].GetCellWidth());

            if (await console.Input.ReadKeyAsync(true, cancellation) is not { } key) return null;
            if (key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                if (key.Key == ConsoleKey.C) throw new OperationCanceledException(cancellation);
                if (key.Key is ConsoleKey.D or ConsoleKey.Z && buffer.Length == 0) return null;
                continue;
            }
            switch (key.Key)
            {
                case ConsoleKey.Enter: return value;
                case ConsoleKey.LeftArrow: caret = previous; break;
                case ConsoleKey.RightArrow: caret = next; break;
                case ConsoleKey.Home: caret = 0; break;
                case ConsoleKey.End: caret = buffer.Length; break;
                case ConsoleKey.Backspace when caret > 0:
                    buffer.Remove(previous, caret - previous);
                    caret = previous;
                    break;
                case ConsoleKey.Delete when caret < buffer.Length:
                    buffer.Remove(caret, next - caret);
                    break;
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        buffer.Insert(caret++, key.KeyChar);
                        if (buffer.Length > 65536) return buffer.ToString();
                    }
                    break;
            }
        }
    }

    public static void Commands(IAnsiConsole console, IReadOnlyList<ConversationCommandDto> commands, bool json)
    {
        if (json) { TurnTranscript.WriteEvent(console, new { kind = "commands.list", commands }); return; }
        var table = new Table().Border(TableBorder.Simple).AddColumn("Command").AddColumn("Source").AddColumn("Description");
        foreach (var command in commands)
            table.AddRow(new Text("/" + command.Name + (command.Hint is null ? "" : " " + command.Hint)), new Text(command.Origin), new Text(command.Description));
        console.Write(table);
    }

    public static void Data<T>(IAnsiConsole console, string kind, T data, bool json)
    {
        if (json) TurnTranscript.WriteEvent(console, new { kind, data });
        else console.WriteLine(JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerOptions.Web) { WriteIndented = true }));
    }

    public static void Selected(IAnsiConsole console, AgentSessionDto session, bool json)
    {
        if (json) TurnTranscript.WriteEvent(console, new { kind = "session.selected", session });
        else console.WriteLine($"Session {session.Id} | {session.Model}");
    }

    public static void History(IAnsiConsole console, IReadOnlyList<AgentMessageDto> messages, bool json)
    {
        if (json) TurnTranscript.WriteEvent(console, new { kind = "session.history", messages });
        else foreach (var message in messages) console.WriteLine($"{message.Role}: {message.Text}");
    }
}