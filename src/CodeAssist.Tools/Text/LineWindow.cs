using System.Text;

namespace CodeAssist.Tools.Text;

/// <param name="Text">Formatted lines.</param>
/// <param name="FirstLine">1-based number of the first line returned, or 0 when none.</param>
/// <param name="LastLine">1-based number of the last line returned.</param>
/// <param name="NextOffset">Offset that continues the listing, or null when the end was reached.</param>
public sealed record LineSlice(string Text, int FirstLine, int LastLine, int? NextOffset);

/// <summary>Pages a sequence of lines by number. The sequence is read lazily, so large files are never loaded whole.</summary>
public static class LineWindow
{
    public static LineSlice Slice(IEnumerable<string> lines, int offset, int limit, int maxLineChars, int maxBytes, bool numbered = true)
    {
        var builder = new StringBuilder();
        var bytes = 0;
        var number = 0;
        var first = 0;
        var last = 0;
        int? next = null;

        foreach (var line in lines)
        {
            number++;
            if (number < offset) continue;

            if (first > 0 && last - first + 1 >= limit)
            {
                next = number;
                break;
            }

            var shown = line.Length > maxLineChars ? line[..maxLineChars] + $"... (+{line.Length - maxLineChars} chars)" : line;
            var formatted = numbered ? $"{number}: {shown}" : shown;
            var size = Encoding.UTF8.GetByteCount(formatted) + 1;
            if (first > 0 && bytes + size > maxBytes)
            {
                next = number;
                break;
            }

            if (first == 0) first = number;
            last = number;
            bytes += size;
            builder.Append(formatted).Append('\n');
        }

        if (builder.Length > 0) builder.Length--;
        return new LineSlice(builder.ToString(), first, last, next);
    }
}
