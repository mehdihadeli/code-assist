using System.Text;

namespace CodeAssist.Tools.Text;

public sealed record ClippedText(string Text, bool Truncated, int OmittedLines, int OmittedChars);

/// <summary>Keeps tool output inside a line/byte budget so one result cannot flood the context.</summary>
public static class OutputBudget
{
    /// <summary>Keeps the start of the text (file content, web pages).</summary>
    public static ClippedText Head(string text, int maxLines, int maxBytes)
    {
        var bytes = 0;
        var lines = 0;
        var position = 0;
        while (position < text.Length)
        {
            var end = text.IndexOf('\n', position);
            var next = end < 0 ? text.Length : end + 1;
            var lineBytes = Encoding.UTF8.GetByteCount(text.AsSpan(position, next - position));
            if (lines >= maxLines) break;
            if (bytes + lineBytes > maxBytes)
            {
                if (lines == 0)
                {
                    foreach (var rune in text.AsSpan(position, next - position).EnumerateRunes())
                    {
                        if (bytes + rune.Utf8SequenceLength > maxBytes) break;
                        bytes += rune.Utf8SequenceLength;
                        position += rune.Utf16SequenceLength;
                    }
                }
                break;
            }
            bytes += lineBytes;
            lines++;
            position = next;
        }

        if (position >= text.Length) return new ClippedText(text, false, 0, 0);

        var omittedLines = CountLines(text.AsSpan(position));
        return new ClippedText(text[..position], true, omittedLines, text.Length - position);
    }

    /// <summary>Keeps the end of the text (command output, where the last lines matter most).</summary>
    public static ClippedText Tail(string text, int maxChars)
    {
        if (text.Length <= maxChars) return new ClippedText(text, false, 0, 0);

        var start = text.Length - maxChars;
        var lineStart = text.IndexOf('\n', start);
        if (lineStart >= 0 && lineStart - start < maxChars / 4) start = lineStart + 1;
        return new ClippedText(text[start..], true, CountLines(text.AsSpan(0, start)), start);
    }

    private static int CountLines(ReadOnlySpan<char> text)
    {
        var count = 0;
        foreach (var c in text) if (c == '\n') count++;
        return text.Length > 0 && text[^1] != '\n' ? count + 1 : count;
    }
}
