using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Workspaces.Platform;

namespace CodeAssist.Tools;

public sealed class GitWorkspaceBranches(IProcessRunner runner, Func<IReadOnlyCollection<string>>? activeRoots = null) : IWorkspaceBranches
{
    private static readonly SemaphoreSlim Mutation = new(1, 1);
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public async Task<string> DestinationAsync(string name, ToolContext context, CancellationToken cancellationToken)
    {
        ValidateName(name);
        var (root, _) = await RepositoryAsync(context, cancellationToken);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..12];
        return Path.Combine(Path.GetDirectoryName(root)!, ".code-assist-worktrees", hash, name);
    }

    public async Task<IReadOnlyList<WorkspaceBranch>> ListAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var (root, common) = await RepositoryAsync(context, cancellationToken);
        var managed = await RegistrationsAsync(common, cancellationToken);
        var output = await GitAsync(root, ["worktree", "list", "--porcelain", "-z"], context, cancellationToken);
        var result = new List<WorkspaceBranch>();
        string? directory = null;
        string? branch = null;
        foreach (var field in output.Split('\0'))
        {
            if (field.StartsWith("worktree ", StringComparison.Ordinal))
            {
                if (directory is not null) result.Add(new(directory, branch, managed.Contains(directory)));
                directory = Path.GetFullPath(field[9..]);
                branch = null;
            }
            else if (field.StartsWith("branch ", StringComparison.Ordinal)) branch = field[7..];
        }
        if (directory is not null) result.Add(new(directory, branch, managed.Contains(directory)));
        return result;
    }

    public async Task<WorkspaceBranch> CreateAsync(string name, ToolContext context, CancellationToken cancellationToken)
    {
        ValidateName(name);
        await Mutation.WaitAsync(cancellationToken);
        try
        {
            var (root, common) = await RepositoryAsync(context, cancellationToken);
            var destination = await DestinationAsync(name, context, cancellationToken);
            var parent = Path.GetDirectoryName(destination)!;
            if (new PathGuard(Path.GetDirectoryName(root)!).Resolve(destination).Outside)
                throw new IOException("Worktree destination escapes through a symbolic link.");
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Worktree destination already exists.");
            var managed = await RegistrationsAsync(common, cancellationToken);
            Directory.CreateDirectory(parent);
            await GitAsync(root, ["worktree", "add", "--detach", "--", destination, "HEAD"], context, cancellationToken);
            managed.Add(destination);
            try { await SaveAsync(common, managed, CancellationToken.None); }
            catch
            {
                await GitAsync(root, ["worktree", "remove", "--", destination], context, CancellationToken.None);
                throw;
            }
            return new(destination, null, true);
        }
        finally { Mutation.Release(); }
    }

    public async Task RemoveAsync(string directory, bool force, ToolContext context, CancellationToken cancellationToken)
    {
        await Mutation.WaitAsync(cancellationToken);
        try
        {
            var (root, common) = await RepositoryAsync(context, cancellationToken);
            var target = new PathGuard(context.Workspace).Resolve(directory).Full;
            var managed = await RegistrationsAsync(common, cancellationToken);
            if (!managed.Contains(target)) throw new IOException("Worktree was not registered by CodeAssist for this project.");
            var active = (activeRoots?.Invoke() ?? []).Append(context.Workspace).Append(root);
            if (active.Any(path => !new PathGuard(target).Resolve(Path.GetFullPath(path)).Outside))
                throw new IOException("Cannot remove a current or active session workspace.");
            var listed = await ListAsync(context, cancellationToken);
            if (!listed.Any(item => Paths.Equals(item.Directory, target))) throw new IOException("Worktree is no longer registered with Git.");
            var status = await GitAsync(target, ["status", "--porcelain", "--untracked-files=all"], context, cancellationToken);
            if (!force && !string.IsNullOrWhiteSpace(status)) throw new IOException("Worktree contains modified or untracked files; removal refused.");
            var originHead = (await GitAsync(root, ["rev-parse", "HEAD"], context, cancellationToken)).Trim();
            var uniqueCommits = await GitAsync(target, ["rev-list", "--max-count=1", "HEAD", "^" + originHead], context, cancellationToken);
            if (!string.IsNullOrWhiteSpace(uniqueCommits)) throw new IOException("Worktree has commits not reachable from the original workspace HEAD; retain it or merge them before removal.");
            await GitAsync(root, force ? ["worktree", "remove", "--force", "--", target] : ["worktree", "remove", "--", target], context, cancellationToken);
            managed.Remove(target);
            await SaveAsync(common, managed, CancellationToken.None);
        }
        finally { Mutation.Release(); }
    }

    private async Task<(string Root, string Common)> RepositoryAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath((await GitAsync(context.Workspace, ["rev-parse", "--show-toplevel"], context, cancellationToken)).Trim());
        var common = Path.GetFullPath((await GitAsync(root, ["rev-parse", "--path-format=absolute", "--git-common-dir"], context, cancellationToken)).Trim());
        return (root, common);
    }

    private async Task<string> GitAsync(string cwd, IReadOnlyList<string> arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var outcome = await runner.RunAsync(new("git", ["-c", "core.hooksPath=", .. arguments], cwd,
            context.Limits.ProcessTimeout, Math.Max(context.Limits.MaxProcessOutputChars, 65536), []), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (outcome.ExitCode != 0 || outcome.TimedOut || outcome.OmittedChars > 0) throw new IOException("Git worktree operation failed or exceeded its output/time limit.");
        return outcome.Output.TrimEnd('\r', '\n');
    }

    private static async Task<HashSet<string>> RegistrationsAsync(string common, CancellationToken cancellationToken)
    {
        var path = Path.Combine(common, "code-assist-worktrees.json");
        if (!File.Exists(path)) return new(Paths);
        var info = new FileInfo(path);
        if (info.Length > 1_048_576 || info.LinkTarget is not null) throw new IOException("Invalid worktree registry.");
        try { return new(JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(path, cancellationToken)) ?? [], Paths); }
        catch (JsonException exception) { throw new IOException("Invalid worktree registry.", exception); }
    }

    private static async Task SaveAsync(string common, HashSet<string> managed, CancellationToken cancellationToken)
    {
        var path = Path.Combine(common, "code-assist-worktrees.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(managed.Order(Paths)), cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ValidateName(string name)
    {
        if (name.Length is < 1 or > 64 || !char.IsAsciiLetterOrDigit(name[0]) ||
            name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("Worktree name must be 1-64 ASCII letters, digits, hyphens or underscores, starting with a letter or digit.", nameof(name));
    }
}