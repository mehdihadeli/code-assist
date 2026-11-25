using CodeAssist.Tools;
using CodeAssist.Workspaces.Platform;

namespace CodeAssist.Tools;

public sealed class GitWorkspaceChanges(IProcessRunner runner) : IWorkspaceChanges
{
    public async Task<IReadOnlyList<WorkspaceChange>> ReadAsync(string state, bool includeDiffs, ToolContext context, CancellationToken cancellationToken)
    {
        if (state is not ("all" or "staged" or "unstaged" or "conflicts")) throw new ArgumentException("Unknown Git change state.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(context.Limits.ProcessTimeout);
        var root = Path.GetFullPath((await GitAsync(["rev-parse", "--show-toplevel"], false, context, deadline.Token)).Trim());
        if (!string.Equals(root.TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(context.Workspace).TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new UnauthorizedAccessException("Git inspection requires the repository/worktree root as its workspace.");
        var status = await GitAsync(["status", "--porcelain=v1", "-z", "--untracked-files=all"], false, context, deadline.Token);
        var fields = status.Split('\0');
        var guard = new PathGuard(context.Workspace, [.. context.ProtectedGlobs, .. context.IgnoreGlobs]);
        var changes = new List<WorkspaceChange>();
        for (var index = 0; index < fields.Length && fields[index].Length > 0; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var field = fields[index];
            if (field.Length < 4 || field[2] != ' ') throw new IOException("Malformed Git status record.");
            var path = field[3..];
            string? original = null;
            if (field[0] is 'R' or 'C' || field[1] is 'R' or 'C')
            {
                if (++index >= fields.Length || fields[index].Length == 0) throw new IOException("Missing Git rename source.");
                original = fields[index];
            }
            var resolved = guard.Resolve(path);
            if (resolved.Outside || resolved.Protected || original is not null && guard.Resolve(original) is { } source && (source.Outside || source.Protected)) continue;
            var conflict = field[0] == 'U' || field[1] == 'U' || field[..2] is "AA" or "DD";
            if (state == "conflicts" && !conflict || state == "staged" && field[0] is ' ' or '?' || state == "unstaged" && field[1] == ' ') continue;
            string? staged = null;
            string? working = null;
            if (includeDiffs && field[0] is not (' ' or '?')) staged = await GitAsync(["diff", "--cached", "--no-ext-diff", "--no-textconv", "--", path], true, context, deadline.Token);
            if (includeDiffs && field[1] is not (' ' or '?')) working = await GitAsync(["diff", "--no-ext-diff", "--no-textconv", "--", path], true, context, deadline.Token);
            changes.Add(new(resolved.Relative, original, field[0].ToString(), field[1].ToString(), conflict, staged, working));
            if (changes.Count > Math.Min(context.Limits.MaxSearchResults, 500)) throw new IOException("Git change capacity exceeded; narrow the repository before inspection.");
        }
        return changes.OrderBy(change => change.Path, StringComparer.Ordinal).ToArray();
    }

    private async Task<string> GitAsync(IReadOnlyList<string> arguments, bool allowTruncation, ToolContext context, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new("git", ["--no-pager", "--literal-pathspecs", "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false", .. arguments], context.Workspace, context.Limits.ProcessTimeout, Math.Min(context.Limits.MaxProcessOutputChars, 1_048_576), context.Limits.AllowedEnvironment), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.ExitCode != 0 || result.TimedOut || result.Cancelled) throw new IOException("Git inspection failed or exceeded its deadline.");
        if (result.OmittedChars > 0 && !allowTruncation) throw new IOException("Git status exceeds its capture limit; no partial inventory returned.");
        return result.OmittedChars > 0 ? "[diff capture truncated; retained tail follows]\n" + result.Output : result.Output;
    }
}