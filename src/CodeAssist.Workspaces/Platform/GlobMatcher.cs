using System.Text;
using System.Text.RegularExpressions;

namespace CodeAssist.Workspaces.Platform;

/// <summary>
/// Glob matching over '/'-separated relative paths. <c>**</c> crosses directories, <c>*</c> and <c>?</c> do not, <c>{a,b}</c> alternates.
/// A pattern without '/' matches any single path segment, so <c>.env*</c> covers <c>sub/.env.local</c>.
/// </summary>
public sealed class GlobMatcher
{
    private readonly List<(Regex Pattern, bool AnySegment)> _patterns = [];

    public GlobMatcher(IEnumerable<string> patterns)
    {
        var options = RegexOptions.CultureInvariant;
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) options |= RegexOptions.IgnoreCase;

        foreach (var raw in patterns)
        {
            var pattern = raw.Trim().Replace('\\', '/');
            if (pattern.Length == 0) continue;
            var anySegment = !pattern.Contains('/');
            _patterns.Add((new Regex("^" + ToRegexSource(pattern) + "$", options, TimeSpan.FromSeconds(1)), anySegment));
        }
    }

    public bool IsEmpty => _patterns.Count == 0;

    public bool IsMatch(string relativePath)
    {
        var path = relativePath.Replace('\\', '/').TrimStart('/');
        foreach (var (pattern, anySegment) in _patterns)
        {
            if (anySegment)
            {
                foreach (var segment in path.Split('/'))
                    if (pattern.IsMatch(segment)) return true;
            }
            else if (pattern.IsMatch(path)) return true;
        }
        return false;
    }

    /// <summary>True when everything below the directory is excluded (for example <c>node_modules/**</c> excludes <c>node_modules</c>).</summary>
    public bool ExcludesDirectory(string relativeDirectory) => IsMatch(relativeDirectory.TrimEnd('/', '\\') + "/_");

    private static string ToRegexSource(string glob)
    {
        var builder = new StringBuilder();
        var depth = 0;
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/') { i++; builder.Append("(?:.*/)?"); }
                    else builder.Append(".*");
                    break;
                case '*': builder.Append("[^/]*"); break;
                case '?': builder.Append("[^/]"); break;
                case '{': depth++; builder.Append("(?:"); break;
                case '}' when depth > 0: depth--; builder.Append(')'); break;
                case ',' when depth > 0: builder.Append('|'); break;
                default: builder.Append(Regex.Escape(c.ToString())); break;
            }
        }
        while (depth-- > 0) builder.Append(')');
        return builder.ToString();
    }
}
