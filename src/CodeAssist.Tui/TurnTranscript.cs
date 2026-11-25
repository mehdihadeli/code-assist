using System.Text.Json;
using CodeAssist.Contracts;
using Spectre.Console;

namespace CodeAssist.Tui;

public enum TurnOutcome { Completed, Failed, Canceled }

public static class TurnTranscript
{
    public static void WriteEvent<T>(IAnsiConsole console, T value) =>
        console.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(value, JsonSerializerOptions.Web));

    public static async Task<TurnOutcome> RenderAsync(IAnsiConsole console, IAsyncEnumerable<AgentTurnEvent> updates, bool json, CancellationToken cancellation = default)
    {
        var writer = console.Profile.Out.Writer;
        var wroteText = false;
        try
        {
            await foreach (var update in updates.WithCancellation(cancellation))
            {
                if (json) WriteEvent(console, update);
                else if (update.Kind == "text.delta")
                {
                    writer.Write(update.Text);
                    wroteText = true;
                }
                await writer.FlushAsync(cancellation);
                if (update.Kind == "turn.completed") return TurnOutcome.Completed;
                if (update.Kind is "turn.failed" or "turn.canceled")
                {
                    if (!json) writer.WriteLine(update.Kind == "turn.canceled" ? "Turn canceled." : "Turn failed. Read committed history before retrying.");
                    return update.Kind == "turn.canceled" ? TurnOutcome.Canceled : TurnOutcome.Failed;
                }
            }
            throw new InvalidDataException("Turn stream ended before completion.");
        }
        finally
        {
            if (!json && wroteText) writer.WriteLine();
        }
    }
}