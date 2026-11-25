using CodeAssist.Configuration;

namespace CodeAssist.Cli.Presentation;

/// <summary>A user-typed setting path resolved against the key catalog.</summary>
public sealed record ResolvedPath(string Text, string[] Segments, KeyNode Node);

public static class PathArgument
{
    public static bool TryResolve(string input, out ResolvedPath? resolved, out string? error)
    {
        resolved = null;
        error = null;
        IReadOnlyList<string> parsed;
        try { parsed = SettingsPath.Parse(input); }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }

        var node = KeyCatalog.Default.Canonicalize(parsed, out var canonical);
        if (node is null)
        {
            error = $"'{input}' is not a setting." + Suggest(parsed);
            return false;
        }
        resolved = new ResolvedPath(SettingsPath.Format(canonical), canonical, node);
        return true;
    }

    private static string Suggest(IReadOnlyList<string> parsed)
    {
        // Offer sibling keys under the deepest valid parent.
        var parent = KeyCatalog.Default.Root;
        foreach (var segment in parsed)
        {
            var next = parent.Step(segment);
            if (next is null) break;
            parent = next;
        }
        if (parent.Kind != KeyKind.Object) return "";
        var typed = parsed.FirstOrDefault(s => parent.Step(s) is null) ?? "";
        var close = parent.Children.Keys
            .Where(k => k.StartsWith(typed.Length > 0 ? typed[..1] : "", StringComparison.OrdinalIgnoreCase) || k.Contains(typed, StringComparison.OrdinalIgnoreCase))
            .Take(5)
            .ToArray();
        return close.Length == 0 ? "" : $" Did you mean: {string.Join(", ", close)}?";
    }
}
