namespace CodeAssist.Tools.Text;

public enum EditFailure { None, EmptyOldText, NotFound, Ambiguous }

/// <param name="Matches">How many places matched the old text.</param>
/// <param name="Replacements">How many were replaced.</param>
public sealed record EditOutcome(EditFailure Failure, string Content, int Matches, int Replacements)
{
    public bool Ok => Failure == EditFailure.None;
}

/// <summary>
/// Exact-string replacement. A single match is required unless <c>replaceAll</c>. A file with CRLF line endings still matches
/// multi-line text written with LF. <c>tolerant</c> additionally ignores trailing spaces and curly quotes, dashes and
/// non-breaking spaces, comparing whole lines.
/// </summary>
public static class TextEdit
{
    public static EditOutcome Replace(string content, string oldText, string newText, bool replaceAll = false, bool tolerant = false)
    {
        if (oldText.Length == 0) return new EditOutcome(EditFailure.EmptyOldText, content, 0, 0);

        var outcome = ReplaceExactly(content, oldText, newText, replaceAll);
        if (outcome.Failure != EditFailure.NotFound) return outcome;

        if (content.Contains("\r\n") && !oldText.Contains('\r') && oldText.Contains('\n'))
        {
            var adapted = ReplaceExactly(content, oldText.Replace("\n", "\r\n"), newText.Replace("\r\n", "\n").Replace("\n", "\r\n"), replaceAll);
            if (adapted.Failure != EditFailure.NotFound) return adapted;
        }

        return tolerant ? ReplaceIgnoringLookalikes(content, oldText, newText, replaceAll) : outcome;
    }

    private static EditOutcome ReplaceExactly(string content, string oldText, string newText, bool replaceAll)
    {
        var count = CountOccurrences(content, oldText);
        if (count == 0) return new EditOutcome(EditFailure.NotFound, content, 0, 0);
        if (count > 1 && !replaceAll) return new EditOutcome(EditFailure.Ambiguous, content, count, 0);

        var index = content.IndexOf(oldText, StringComparison.Ordinal);
        var result = replaceAll
            ? content.Replace(oldText, newText, StringComparison.Ordinal)
            : string.Concat(content.AsSpan(0, index), newText, content.AsSpan(index + oldText.Length));
        return new EditOutcome(EditFailure.None, result, count, replaceAll ? count : 1);
    }

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        for (var index = content.IndexOf(value, StringComparison.Ordinal); index >= 0; index = content.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private readonly record struct Line(string Text, string Eol);

    private static EditOutcome ReplaceIgnoringLookalikes(string content, string oldText, string newText, bool replaceAll)
    {
        var lines = SplitLinesWithEndings(content);
        var oldLines = SplitLinesWithoutEndings(oldText);
        if (oldLines.Count == 0) return new EditOutcome(EditFailure.NotFound, content, 0, 0);

        var starts = FindMatchingLineBlocks(lines, oldLines.Select(NormalizeForComparison).ToArray());

        if (starts.Count == 0) return new EditOutcome(EditFailure.NotFound, content, 0, 0);
        if (starts.Count > 1 && !replaceAll) return new EditOutcome(EditFailure.Ambiguous, content, starts.Count, 0);

        var replacement = SplitLinesWithoutEndings(newText);
        var dominant = lines.Count(l => l.Eol == "\r\n") > lines.Count(l => l.Eol == "\n") ? "\r\n" : "\n";
        var output = new System.Text.StringBuilder(content.Length);
        var cursor = 0;
        foreach (var start in starts)
        {
            AppendLines(output, lines.GetRange(cursor, start - cursor));
            var lastEol = lines[start + oldLines.Count - 1].Eol;
            foreach (var (position, text) in replacement.Index())
                output.Append(text).Append(position == replacement.Count - 1 ? lastEol : dominant);
            cursor = start + oldLines.Count;
        }
        AppendLines(output, lines.GetRange(cursor, lines.Count - cursor));

        var replaced = replaceAll ? starts.Count : 1;
        return new EditOutcome(EditFailure.None, output.ToString(), starts.Count, replaced);
    }

    /// <summary>Start indexes of non-overlapping blocks of lines equal to the wanted lines after normalization.</summary>
    private static List<int> FindMatchingLineBlocks(List<Line> lines, string[] wantedNormalized)
    {
        var starts = new List<int>();
        var start = 0;
        while (start + wantedNormalized.Length <= lines.Count)
        {
            var isMatch = lines.GetRange(start, wantedNormalized.Length)
                .Select(line => NormalizeForComparison(line.Text))
                .SequenceEqual(wantedNormalized);
            if (isMatch)
            {
                starts.Add(start);
                start += wantedNormalized.Length;
            }
            else
            {
                start++;
            }
        }
        return starts;
    }

    private static void AppendLines(System.Text.StringBuilder output, IEnumerable<Line> lines)
    {
        foreach (var line in lines) output.Append(line.Text).Append(line.Eol);
    }

    private static List<Line> SplitLinesWithEndings(string content)
    {
        var lines = new List<Line>();
        var start = 0;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != '\n') continue;
            var crlf = i > start && content[i - 1] == '\r';
            lines.Add(new Line(content[start..(crlf ? i - 1 : i)], crlf ? "\r\n" : "\n"));
            start = i + 1;
        }
        if (start < content.Length) lines.Add(new Line(content[start..], ""));
        return lines;
    }

    private static List<string> SplitLinesWithoutEndings(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static string NormalizeForComparison(string line) =>
        new(line.TrimEnd().Select(FoldLookalike).ToArray());

    /// <summary>Maps curly quotes, dashes and special spaces to their plain ASCII look-alikes.</summary>
    private static char FoldLookalike(char c) => c switch
    {
        '\u2018' or '\u2019' => '\'',
        '\u201C' or '\u201D' => '"',
        '\u2013' or '\u2014' or '\u2212' => '-',
        '\u00A0' or '\u2007' or '\u202F' => ' ',
        _ => c,
    };
}
