using System.Text;

namespace CodeAssist.Tools.Text;

public sealed record DiffSummary(int Added, int Removed);

/// <summary>Line diff in unified format, for approval previews and result summaries. Pure and bounded.</summary>
public static class UnifiedDiff
{
    /// <summary>Above this many table cells the changed region is shown as a full replacement instead of an exact diff.</summary>
    private const int MaxTableCells = 4_000_000;

    private enum EditKind { Keep, Remove, Add }

    private readonly record struct LineEdit(EditKind Kind, string Text)
    {
        public char Marker => Kind switch { EditKind.Remove => '-', EditKind.Add => '+', _ => ' ' };
    }

    /// <summary>A run of changes close enough to share one hunk, as indexes into the edit script.</summary>
    private readonly record struct ChangeGroup(int FirstChange, int LastChange);

    public static DiffSummary Count(string? before, string after)
    {
        var edits = BuildEdits(before, after);
        return new DiffSummary(edits.Count(e => e.Kind == EditKind.Add), edits.Count(e => e.Kind == EditKind.Remove));
    }

    /// <param name="before">Old content, or null for a new file.</param>
    public static string Create(string path, string? before, string after, int context = 3, int maxLines = 300, bool isDeletion = false)
    {
        var edits = BuildEdits(before, after);
        var changeIndexes = edits.Index().Where(e => e.Item.Kind != EditKind.Keep).Select(e => e.Index).ToList();
        if (changeIndexes.Count == 0) return "";

        var (oldLinesBefore, newLinesBefore) = CountLinesBeforeEachEdit(edits);

        var builder = new StringBuilder();
        builder.Append("--- ").AppendLine(before is null ? "/dev/null" : "a/" + path);
        builder.Append("+++ ").AppendLine(isDeletion ? "/dev/null" : "b/" + path);

        var emittedLines = 0;
        foreach (var group in GroupNearbyChanges(changeIndexes, context))
        {
            var start = Math.Max(0, group.FirstChange - context);
            var end = Math.Min(edits.Count, group.LastChange + context + 1);
            AppendHunkHeader(builder, oldLinesBefore, newLinesBefore, start, end);

            for (var index = start; index < end; index++)
            {
                if (emittedLines++ >= maxLines)
                {
                    builder.AppendLine("... (diff truncated)");
                    return builder.ToString().TrimEnd();
                }
                var edit = edits[index];
                builder.Append(edit.Marker).AppendLine(edit.Text);
                var oldEnd = edit.Kind != EditKind.Add && oldLinesBefore[index + 1] == oldLinesBefore[^1] && before is { Length: > 0 } && !before.EndsWith('\n');
                var newEnd = edit.Kind != EditKind.Remove && newLinesBefore[index + 1] == newLinesBefore[^1] && after.Length > 0 && !after.EndsWith('\n');
                if (oldEnd || newEnd) builder.AppendLine("\\ No newline at end of file");
            }
        }
        return builder.ToString().TrimEnd();
    }

    private static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var normalized = text.Replace("\r\n", "\n");
        return (normalized.EndsWith('\n') ? normalized[..^1] : normalized).Split('\n');
    }

    private static List<LineEdit> BuildEdits(string? before, string after)
    {
        var edits = BuildEditScript(SplitLines(before), SplitLines(after));
        if (before is { Length: > 0 } && after.Length > 0 && before.EndsWith('\n') != after.EndsWith('\n') && edits.Count > 0 && edits[^1].Kind == EditKind.Keep)
        {
            var last = edits[^1];
            edits.RemoveAt(edits.Count - 1);
            edits.Add(new LineEdit(EditKind.Remove, last.Text));
            edits.Add(new LineEdit(EditKind.Add, last.Text));
        }
        return edits;
    }

    /// <summary>Turns two line lists into the sequence of keep/remove/add steps that transforms the first into the second.</summary>
    private static List<LineEdit> BuildEditScript(string[] oldLines, string[] newLines)
    {
        var commonStart = CommonPrefixLength(oldLines, newLines);
        var commonEnd = CommonSuffixLength(oldLines, newLines, commonStart);

        var script = new List<LineEdit>(oldLines.Length + newLines.Length);
        script.AddRange(oldLines.Take(commonStart).Select(line => new LineEdit(EditKind.Keep, line)));

        var oldMiddle = oldLines[commonStart..^commonEnd];
        var newMiddle = newLines[commonStart..^commonEnd];
        if ((long)(oldMiddle.Length + 1) * (newMiddle.Length + 1) > MaxTableCells)
        {
            script.AddRange(oldMiddle.Select(line => new LineEdit(EditKind.Remove, line)));
            script.AddRange(newMiddle.Select(line => new LineEdit(EditKind.Add, line)));
        }
        else
        {
            script.AddRange(DiffByLongestCommonSubsequence(oldMiddle, newMiddle));
        }

        script.AddRange(oldLines.TakeLast(commonEnd).Select(line => new LineEdit(EditKind.Keep, line)));
        return script;
    }

    private static int CommonPrefixLength(string[] first, string[] second)
    {
        var length = 0;
        while (length < first.Length && length < second.Length && first[length] == second[length]) length++;
        return length;
    }

    private static int CommonSuffixLength(string[] first, string[] second, int alreadyMatchedAtStart)
    {
        var length = 0;
        var room = Math.Min(first.Length, second.Length) - alreadyMatchedAtStart;
        while (length < room && first[^(length + 1)] == second[^(length + 1)]) length++;
        return length;
    }

    /// <summary>Exact line diff: keeps the longest common run of lines and removes/adds the rest.</summary>
    private static List<LineEdit> DiffByLongestCommonSubsequence(string[] oldLines, string[] newLines)
    {
        var commonLengths = BuildCommonLengthTable(oldLines, newLines);

        var script = new List<LineEdit>();
        int oldIndex = 0, newIndex = 0;
        while (oldIndex < oldLines.Length && newIndex < newLines.Length)
        {
            if (oldLines[oldIndex] == newLines[newIndex])
            {
                script.Add(new LineEdit(EditKind.Keep, oldLines[oldIndex++]));
                newIndex++;
            }
            else if (commonLengths[oldIndex + 1, newIndex] >= commonLengths[oldIndex, newIndex + 1])
            {
                script.Add(new LineEdit(EditKind.Remove, oldLines[oldIndex++]));
            }
            else
            {
                script.Add(new LineEdit(EditKind.Add, newLines[newIndex++]));
            }
        }

        script.AddRange(oldLines.Skip(oldIndex).Select(line => new LineEdit(EditKind.Remove, line)));
        script.AddRange(newLines.Skip(newIndex).Select(line => new LineEdit(EditKind.Add, line)));
        return script;
    }

    /// <summary>Cell [i, j] holds how many lines the suffixes oldLines[i..] and newLines[j..] have in common (in order).</summary>
    private static int[,] BuildCommonLengthTable(string[] oldLines, string[] newLines)
    {
        var table = new int[oldLines.Length + 1, newLines.Length + 1];
        for (var i = oldLines.Length - 1; i >= 0; i--)
        {
            for (var j = newLines.Length - 1; j >= 0; j--)
            {
                table[i, j] = oldLines[i] == newLines[j]
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }
        return table;
    }

    /// <summary>For each position in the script, how many old and new lines come before it (used for hunk line numbers).</summary>
    private static (int[] OldLines, int[] NewLines) CountLinesBeforeEachEdit(List<LineEdit> edits)
    {
        var oldLines = new List<int> { 0 };
        var newLines = new List<int> { 0 };
        foreach (var edit in edits)
        {
            oldLines.Add(oldLines[^1] + (edit.Kind == EditKind.Add ? 0 : 1));
            newLines.Add(newLines[^1] + (edit.Kind == EditKind.Remove ? 0 : 1));
        }
        return (oldLines.ToArray(), newLines.ToArray());
    }

    /// <summary>Changes whose context would overlap or touch end up in the same hunk.</summary>
    private static List<ChangeGroup> GroupNearbyChanges(List<int> changeIndexes, int context)
    {
        var groups = new List<ChangeGroup>();
        var first = changeIndexes[0];
        var previous = first;
        foreach (var index in changeIndexes.Skip(1))
        {
            if (index - previous > 2 * context)
            {
                groups.Add(new ChangeGroup(first, previous));
                first = index;
            }
            previous = index;
        }
        groups.Add(new ChangeGroup(first, previous));
        return groups;
    }

    private static void AppendHunkHeader(StringBuilder builder, int[] oldLinesBefore, int[] newLinesBefore, int start, int end)
    {
        var oldCount = oldLinesBefore[end] - oldLinesBefore[start];
        var newCount = newLinesBefore[end] - newLinesBefore[start];
        // A zero-length side points at the line before the hunk, as in standard unified diffs.
        var oldStart = oldCount == 0 ? oldLinesBefore[start] : oldLinesBefore[start] + 1;
        var newStart = newCount == 0 ? newLinesBefore[start] : newLinesBefore[start] + 1;
        builder.Append("@@ -").Append(oldStart).Append(',').Append(oldCount)
            .Append(" +").Append(newStart).Append(',').Append(newCount).AppendLine(" @@");
    }
}
