namespace CodeAssist.Extensions.Plugins;

public sealed record PluginFit
{
    public List<string> FileTypes { get; init; } = [];
    public List<string> Folders { get; init; } = [];
    public List<string> Commands { get; init; } = [];
    public List<string> Dependencies { get; init; } = [];

    internal void Validate()
    {
        foreach (var values in new[] { FileTypes, Folders, Commands, Dependencies })
            if (values is null || values.Count > 24 || values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 160 || value.Any(char.IsControl)))
                throw new InvalidDataException("Plugin fit signals require at most 24 literal values per family, each up to 160 printable characters.");
        if (FileTypes.Any(value => !value.StartsWith('.') || value.Length < 2 || value.IndexOfAny(['/', '\\', '*', '?']) >= 0))
            throw new InvalidDataException("Plugin file types must be literal suffixes such as .cs or .csproj.");
        foreach (var folder in Folders) PluginFiles.Relative(folder);
        if (Commands.Any(value => value.Any(char.IsWhiteSpace) || value.IndexOfAny(['/', '\\', '*', '?']) >= 0))
            throw new InvalidDataException("Plugin command signals must be literal executable names, not shell expressions.");
    }

    internal PluginFit Copy() => this with { FileTypes = [.. FileTypes], Folders = [.. Folders], Commands = [.. Commands], Dependencies = [.. Dependencies] };
}

public sealed record PluginActivity(IReadOnlyList<string> Files, IReadOnlyList<string> Folders, IReadOnlyList<string> Commands, IReadOnlyList<string> Dependencies);
public sealed record PluginSuggestion(string Id, string Release, string Lane, string Blurb, IReadOnlyList<string> Reasons);

public static class PluginRecommendations
{
    public static IReadOnlyList<PluginSuggestion> Rank(PluginRegistry registry, PluginActivity activity, IEnumerable<string> installed, string lane = "general")
    {
        foreach (var values in new[] { activity.Files, activity.Folders, activity.Commands, activity.Dependencies })
            if (values is null || values.Count > 256 || values.Any(value => value is null || value.Length > 1024 || value.Any(char.IsControl)))
                throw new InvalidDataException("Recommendation activity exceeds its 256 facts per family or 1024 characters per fact limit.");
        var excluded = installed.Take(129).ToHashSet(StringComparer.Ordinal);
        if (excluded.Count > 128) throw new InvalidDataException("Recommendation installed set exceeds its limit.");
        return registry.Search(lane: lane).Where(entry => !excluded.Contains(entry.Id)).Select(entry =>
        {
            var fit = entry.Fit;
            var reasons = new List<string>();
            if (fit.FileTypes.Any(suffix => activity.Files.Any(path => path.Replace('\\', '/').EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))) reasons.Add("file-type");
            if (fit.Folders.Any(folder => activity.Folders.Any(path => FolderMatches(folder, path)))) reasons.Add("folder");
            if (fit.Commands.Intersect(activity.Commands, StringComparer.OrdinalIgnoreCase).Any()) reasons.Add("command");
            if (fit.Dependencies.Intersect(activity.Dependencies, StringComparer.OrdinalIgnoreCase).Any()) reasons.Add("dependency");
            return new PluginSuggestion(entry.Id, entry.Release, entry.Lane, entry.Blurb, reasons.ToArray());
        }).Where(item => item.Reasons.Count > 0).OrderByDescending(item => item.Reasons.Count).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
    }

    private static bool FolderMatches(string folder, string observed)
    {
        var path = observed.Replace('\\', '/').TrimEnd('/');
        var expected = folder.TrimEnd('/');
        return path.Equals(expected, StringComparison.OrdinalIgnoreCase) || path.StartsWith(expected + "/", StringComparison.OrdinalIgnoreCase);
    }
}