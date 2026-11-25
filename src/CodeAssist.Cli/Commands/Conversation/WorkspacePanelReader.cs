using System.Diagnostics;
using System.IO.Enumeration;
using System.Text;
using CodeAssist.Tui.FullScreen;

namespace CodeAssist.Cli.Commands.Conversation;

public sealed class WorkspacePanelReader(string directory, IEnumerable<string> excluded, bool consent, Func<string, FileAttributes>? attributes = null)
{
    private readonly string root = Path.GetFullPath(directory);
    private readonly Func<string, FileAttributes> readAttributes = attributes ?? File.GetAttributes;
    private readonly string[] patterns = [".git", ".code-assist", ".env*", "*.key", "*.pem", "*.pfx", "bin", "obj", "node_modules", .. excluded];

    public IReadOnlyList<WorkspaceFileEntry> ReadFiles(CancellationToken cancellation)
    {
        RequireConsent();
        if ((readAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Workspace root is linked.");
        var results = new List<WorkspaceFileEntry>();
        var pending = new Queue<(string Directory, int Depth)>();
        pending.Enqueue((root, 0));
        var scanned = 0;
        while (pending.TryDequeue(out var folder))
        {
            cancellation.ThrowIfCancellationRequested();
            if (folder.Depth > 6) continue;
            foreach (var path in System.IO.Directory.EnumerateFileSystemEntries(folder.Directory))
            {
                cancellation.ThrowIfCancellationRequested();
                if (++scanned > 2048 || results.Count >= 256) return results.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
                if (!IsReadable(path)) continue;
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                var isDirectory = System.IO.Directory.Exists(path);
                results.Add(new(relative + (isDirectory ? "/" : "")));
                if (isDirectory) pending.Enqueue((path, folder.Depth + 1));
            }
        }
        return results.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
    }

    public async Task<IReadOnlyList<string>> ReadPreviewAsync(string path, CancellationToken cancellation)
    {
        RequireConsent();
        var full = Path.GetFullPath(path, root);
        if (!IsReadable(full)) throw new UnauthorizedAccessException("Path is outside the workspace, linked, ignored or protected.");
        await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        if (stream.Length > 65536) throw new IOException("Preview exceeds 64 KiB.");
        var buffer = new byte[65537];
        var length = 0;
        int count;
        while ((count = await stream.ReadAsync(buffer.AsMemory(length), cancellation)) != 0)
        {
            length += count;
            if (length > 65536) throw new IOException("Preview exceeds 64 KiB.");
        }
        if (!IsReadable(full)) throw new UnauthorizedAccessException("Path changed during preview.");
        if (buffer.AsSpan(0, length).Contains((byte)0)) throw new IOException("Binary preview unavailable.");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(buffer, 0, length).TrimStart('\ufeff'); }
        catch (DecoderFallbackException exception) { throw new IOException("Text preview requires valid UTF-8.", exception); }
        return text.Replace("\r", "").Split('\n').Take(120).ToArray();
    }

    public async Task<string?> ReadBranchAsync(CancellationToken cancellation)
    {
        RequireConsent();
        var result = await GitAsync(["symbolic-ref", "--short", "-q", "HEAD"], cancellation);
        return result.Code == 0 ? result.Output.Trim() : null;
    }

    public async Task<IReadOnlyList<WorkspaceFileEntry>> ReadChangesAsync(CancellationToken cancellation)
    {
        RequireConsent();
        var result = await GitAsync(["status", "--porcelain=v1", "-z", "--untracked-files=normal"], cancellation);
        if (result.Code != 0) throw new IOException("Git unavailable or workspace is not a repository.");
        return ParseChanges(result.Output).Where(entry => IsReadable(Path.GetFullPath(entry.Path, root))).ToArray();
    }

    public static IReadOnlyList<WorkspaceFileEntry> ParseChanges(string output)
    {
        if (output.Length > 0 && output[^1] != '\0') throw new IOException("Unterminated Git status record.");
        var fields = output.Split('\0');
        var entries = new List<WorkspaceFileEntry>();
        for (var index = 0; index < fields.Length - 1; index++)
        {
            var field = fields[index];
            if (field.Length < 4 || field[2] != ' ') throw new IOException("Malformed Git status record.");
            var path = field[3..];
            if (path.Any(char.IsControl)) throw new IOException("Unsafe Git status path.");
            if (field[0] is 'R' or 'C' || field[1] is 'R' or 'C')
            {
                if (++index >= fields.Length || fields[index].Length == 0) throw new IOException("Missing Git rename source.");
                if (fields[index].Any(char.IsControl)) throw new IOException("Unsafe Git rename source.");
            }
            if (field[..2] == "??") entries.Add(new(path, "?") { Group = "Untracked" });
            else if (field[0] == 'U' || field[1] == 'U' || field[..2] is "AA" or "DD") entries.Add(new(path, "!") { Group = "Conflicts" });
            else
            {
                if (field[1] != ' ') entries.Add(new(path, field[1].ToString()) { Group = "Unstaged" });
                if (field[0] != ' ') entries.Add(new(path, field[0].ToString()) { Group = "Staged" });
            }
            if (entries.Count > 256) throw new IOException("Git inventory exceeds 256 entries.");
        }
        return entries;
    }

    private bool IsReadable(string full)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, comparison) || relative.Contains(':') || relative.Any(char.IsControl)) return false;
        foreach (var pattern in patterns)
        {
            var normalized = pattern.Replace('\\', '/');
            if (normalized.Contains('{') || normalized.Contains('[')) return false;
            if (FileSystemName.MatchesSimpleExpression(normalized, relative, OperatingSystem.IsWindows()) ||
                FileSystemName.MatchesSimpleExpression(normalized, relative + "/_", OperatingSystem.IsWindows()) ||
                !normalized.Contains('/') && relative.Split('/').Any(segment => FileSystemName.MatchesSimpleExpression(normalized, segment, OperatingSystem.IsWindows()))) return false;
            if (normalized.StartsWith("**/", StringComparison.Ordinal) && FileSystemName.MatchesSimpleExpression(normalized[3..], relative, OperatingSystem.IsWindows())) return false;
        }
        var current = full;
        while (current.Length >= root.Length)
        {
            if (File.Exists(current) || System.IO.Directory.Exists(current))
                if ((readAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) break;
            current = parent;
        }
        return true;
    }

    private void RequireConsent()
    {
        if (!consent) throw new UnauthorizedAccessException("Workspace access required.");
    }

    private async Task<(int Code, string Output)> GitAsync(string[] arguments, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in new[] { "--no-pager", "--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false" }.Concat(arguments)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not inspect Git.");
        try
        {
            var output = CaptureAsync(process.StandardOutput, deadline.Token);
            var error = CaptureAsync(process.StandardError, deadline.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token));
            return (process.ExitCode, await output);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private static async Task<string> CaptureAsync(StreamReader reader, CancellationToken cancellation)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation)) != 0)
        {
            if (result.Length + count > 65536) throw new IOException("Git capture exceeds 64 KiB.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}