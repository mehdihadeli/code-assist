using System.Text;

namespace CodeAssist.Configuration;

/// <summary>Parses and formats setting paths such as <c>providers.openai.models["gpt-4.1"].name</c> or <c>workspace.ignore[0]</c>.</summary>
public static class SettingsPath
{
    public static IReadOnlyList<string> Parse(string text)
    {
        var segments = new List<string>();
        var current = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '.')
            {
                Flush();
                i++;
            }
            else if (c == '[')
            {
                Flush();
                i++;
                if (i < text.Length && text[i] == '"')
                {
                    i++;
                    while (i < text.Length && text[i] != '"')
                    {
                        if (text[i] == '\\' && i + 1 < text.Length) i++;
                        current.Append(text[i++]);
                    }
                    if (i >= text.Length) throw new FormatException("Unterminated quoted segment in path.");
                    i++;
                }
                else
                {
                    while (i < text.Length && text[i] != ']') current.Append(text[i++]);
                }
                if (i >= text.Length || text[i] != ']') throw new FormatException("Missing ']' in path.");
                i++;
                segments.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
                i++;
            }
        }
        Flush();
        if (segments.Count == 0) throw new FormatException("Path is empty.");
        return segments;

        void Flush()
        {
            if (current.Length == 0) return;
            segments.Add(current.ToString());
            current.Clear();
        }
    }

    public static string Format(IReadOnlyList<string> segments)
    {
        var builder = new StringBuilder();
        foreach (var segment in segments)
        {
            if (NeedsQuoting(segment))
            {
                builder.Append("[\"").Append(segment.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"]");
            }
            else
            {
                if (builder.Length > 0) builder.Append('.');
                builder.Append(segment);
            }
        }
        return builder.ToString();
    }

    public static bool StartsWith(IReadOnlyList<string> path, IReadOnlyList<string> prefix)
    {
        if (prefix.Count > path.Count) return false;
        for (var i = 0; i < prefix.Count; i++)
            if (!string.Equals(path[i], prefix[i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static bool NeedsQuoting(string segment) =>
        segment.Length == 0 || segment.Any(c => c is '.' or '[' or ']' or '"' || char.IsWhiteSpace(c));
}
