namespace CodeAssist.Tools.Text;

public sealed record CommandSplit(IReadOnlyList<string> Segments, bool Complex);

/// <summary>
/// Splits a shell command line into the simple commands it runs (separated by &amp;&amp;, ||, ;, |, &amp; or newlines) so policy
/// can judge each one. Quotes are respected. Command substitution and process substitution cannot be split reliably;
/// such lines are reported as <c>Complex</c> and returned whole.
/// </summary>
public static class CommandSplitter
{
    public static CommandSplit Split(string commandLine)
    {
        var line = commandLine.Trim();
        if (line.Length == 0) return new CommandSplit([], false);

        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        char quote = '\0';
        var complex = false;

        void Flush()
        {
            var text = current.ToString().Trim();
            if (text.Length > 0) segments.Add(text);
            current.Clear();
        }

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != '\0')
            {
                current.Append(c);
                if (c == '\\' && quote == '"' && i + 1 < line.Length) current.Append(line[++i]);
                else if (c == quote) quote = '\0';
                else if (quote == '"' && (c == '`' || (c == '$' && i + 1 < line.Length && line[i + 1] == '('))) complex = true;
                continue;
            }

            if (c is '\'' or '"') { quote = c; current.Append(c); continue; }
            if (c == '`' || (c == '$' && i + 1 < line.Length && line[i + 1] == '(') || ((c == '<' || c == '>') && i + 1 < line.Length && line[i + 1] == '(')) complex = true;

            if (c == '\n' || c == ';') { Flush(); continue; }
            if (c == '|' || c == '&')
            {
                var doubled = i + 1 < line.Length && line[i + 1] == c;
                var redirect = c == '&' && (i > 0 && line[i - 1] is '>' or '<' || i + 1 < line.Length && line[i + 1] == '>');
                if (redirect) { current.Append(c); continue; }
                Flush();
                if (doubled) i++;
                continue;
            }
            current.Append(c);
        }
        Flush();

        return complex ? new CommandSplit([line], true) : new CommandSplit(segments, false);
    }
}
