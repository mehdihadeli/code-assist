namespace CodeAssist.Workspaces.Platform;

public sealed record FileFacts(long Length, bool IsDirectory, DateTimeOffset LastWriteUtc);

public sealed record DirectoryEntry(string Name, bool IsDirectory);

/// <summary>File access for tools. Callers resolve and vet paths through <see cref="PathGuard"/> first.</summary>
public interface IFileSystem
{
    FileFacts? Stat(string path);

    Stream OpenRead(string path);

    Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken);

    /// <summary>Writes to a temporary file in the same directory, then renames over the target. Creates missing parent directories.</summary>
    Task WriteAtomicAsync(string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    void DeleteFile(string path);

    void MoveFile(string from, string to);

    IReadOnlyList<DirectoryEntry> ListDirectory(string path);

    /// <summary>All files under a root. Directories rejected by <paramref name="descend"/> and directory symlinks are not entered.</summary>
    IEnumerable<string> EnumerateFiles(string root, Func<string, bool> descend);
}

public sealed class LocalFileSystem : IFileSystem
{
    public FileFacts? Stat(string path)
    {
        if (Directory.Exists(path)) return new FileFacts(0, true, Directory.GetLastWriteTimeUtc(path));
        var file = new FileInfo(path);
        return file.Exists ? new FileFacts(file.Length, false, file.LastWriteTimeUtc) : null;
    }

    public Stream OpenRead(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);

    public async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = OpenRead(path);
        var buffer = new byte[stream.Length];
        await stream.ReadExactlyAsync(buffer, cancellationToken);
        return buffer;
    }

    public async Task WriteAtomicAsync(string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".ca-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(data, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void DeleteFile(string path) => File.Delete(path);

    public void MoveFile(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to) ?? ".");
        File.Move(from, to);
    }

    public IReadOnlyList<DirectoryEntry> ListDirectory(string path) =>
        new DirectoryInfo(path).EnumerateFileSystemInfos()
            .Select(info => new DirectoryEntry(info.Name, info is DirectoryInfo))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IEnumerable<string> EnumerateFiles(string root, Func<string, bool> descend)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            var info = new DirectoryInfo(directory);
            IEnumerable<FileSystemInfo> children;
            try { children = info.EnumerateFileSystemInfos().ToArray(); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { continue; }

            foreach (var child in children.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                if (child is DirectoryInfo)
                {
                    if (child.LinkTarget is null && descend(child.FullName)) pending.Push(child.FullName);
                }
                else yield return child.FullName;
            }
        }
    }
}
