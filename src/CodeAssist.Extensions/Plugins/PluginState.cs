namespace CodeAssist.Extensions.Plugins;

public sealed record PluginInstall(string Id, string Release, string Digest, bool Enabled = false)
{
    public string? Source { get; init; }
}
public sealed record PluginUsage(long Invocations, DateTimeOffset LastUsed);
public sealed record PluginStatus(string Id, string Release, string Digest, bool Enabled, string Summary, int Contributions, long Bytes);

public sealed record PluginLoadIssue(string? Id, string Code, string Message, string Action, bool Blocking);
public sealed record PluginLoadEntry(string Id, string Release, string Digest, bool Selected, bool Required, int Contributions);
public sealed record PluginLoadReport(bool Ready, IReadOnlyList<PluginLoadEntry> Entries, IReadOnlyList<PluginLoadIssue> Issues);

internal sealed record PluginState
{
    public int Schema { get; init; } = 1;
    public List<PluginInstall> Installed { get; init; } = [];
    public Dictionary<string, Dictionary<string, bool>> Workspaces { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, PluginUsage> Usage { get; init; } = new(StringComparer.Ordinal);
}