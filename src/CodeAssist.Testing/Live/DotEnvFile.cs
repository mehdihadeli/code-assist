using System.Text;

namespace CodeAssist.Testing.Live;

/// <summary>Reads KEY=VALUE pairs from a .env file. Supports comments, <c>export</c>, quotes and trailing comments.</summary>
public static class DotEnvFile
{
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();

            var separator = line.IndexOf('=');
            if (separator <= 0) continue;

            var name = line[..separator].Trim();
            if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_') || name.Any(character => !(char.IsLetterOrDigit(character) || character is '_' or '-' or '.' or '/' or ':' or '@'))) continue;
            values[name] = ParseValue(line[(separator + 1)..].Trim());
        }
        return values;
    }

    public static IReadOnlyDictionary<string, string> Read(string path) =>
        File.Exists(path) ? Parse(File.ReadAllText(path)) : new Dictionary<string, string>();

    private static string ParseValue(string raw)
    {
        if (raw.Length > 0 && raw[0] is '"' or '\'')
        {
            var quote = raw[0];
            var value = new StringBuilder();
            for (var index = 1; index < raw.Length; index++)
            {
                var character = raw[index];
                if (character == quote) return value.ToString();
                if (quote == '"' && character == '\\' && index + 1 < raw.Length)
                {
                    var escaped = raw[++index];
                    value.Append(escaped switch { 'n' => "\n", 'r' => "\r", 't' => "\t", '\\' => "\\", '"' => "\"", _ => "\\" + escaped });
                }
                else value.Append(character);
            }
        }

        var comment = raw.IndexOf(" #", StringComparison.Ordinal);
        return (comment >= 0 ? raw[..comment] : raw).Trim();
    }
}
