using System.Text;
using CodeAssist.Tools;

namespace CodeAssist.Sessions;

public sealed class FileToolOutputStore(string directory) : IToolOutputStore
{
    private readonly string _root = Path.GetFullPath(directory);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private const int MaximumBytes = 32 * 1024 * 1024;

    public async Task<Guid> SaveAsync(Guid session, string text, CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaximumBytes) throw new IOException("Output exceeds its 32 MiB retention ceiling.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var folder = Folder(session);
            Directory.CreateDirectory(folder);
            if (Directory.EnumerateFiles(folder).Take(128).Count() >= 128 || Directory.EnumerateFiles(folder).Sum(path => new FileInfo(path).Length) + Encoding.UTF8.GetByteCount(text) > 128L * 1024 * 1024)
                throw new IOException("Session output retention capacity reached.");
            var id = Guid.NewGuid();
            var final = Path.Combine(folder, id.ToString("N") + ".txt");
            var temporary = final + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(false), cancellationToken);
                File.Move(temporary, final);
                return id;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _gate.Release(); }
    }

    public async Task<string> ReadAsync(Guid session, Guid output, int offset, int limit, CancellationToken cancellationToken)
    {
        if (output == Guid.Empty || offset < 1 || limit is < 1 or > 2000) throw new ArgumentException("Invalid output handle or page.");
        var path = Path.Combine(Folder(session), output.ToString("N") + ".txt");
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length > MaximumBytes) throw new IOException("Output unavailable in this session.");
        using var reader = new StreamReader(path, Encoding.UTF8);
        var result = new StringBuilder();
        var number = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (++number < offset) continue;
            if (number >= (long)offset + limit || result.Length > 24000) return result + $"\n[continue with offset={number}]";
            result.Append(number).Append(": ").AppendLine(line[..Math.Min(line.Length, 2000)]);
            if (line.Length > 2000) result.AppendLine("[line clipped; use output.read characterOffset/characters]");
        }
        return result.Length == 0 ? "(end of output)" : result.ToString();
    }

    public async Task<string> ReadCharactersAsync(Guid session, Guid output, int offset, int limit, CancellationToken cancellationToken)
    {
        if (output == Guid.Empty || offset is < 0 or > MaximumBytes || limit is < 1 or > 4000) throw new ArgumentException("Invalid output character page.");
        var path = Path.Combine(Folder(session), output.ToString("N") + ".txt");
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length > MaximumBytes) throw new IOException("Output unavailable in this session.");
        using var reader = new StreamReader(path, Encoding.UTF8);
        var skip = new char[4096];
        var remaining = offset;
        while (remaining > 0)
        {
            var read = await reader.ReadAsync(skip.AsMemory(0, Math.Min(skip.Length, remaining)), cancellationToken);
            if (read == 0) return "(end of output)";
            remaining -= read;
        }
        var buffer = new char[limit + 1];
        var count = await reader.ReadBlockAsync(buffer, cancellationToken);
        if (count == 0) return "(end of output)";
        var shown = Math.Min(count, limit);
        if (shown > 1 && char.IsHighSurrogate(buffer[shown - 1])) shown--;
        return new string(buffer, 0, shown) + (count > shown ? $"\n[continue with characterOffset={offset + shown}]" : "");
    }

    private string Folder(Guid session)
    {
        if (session == Guid.Empty) throw new UnauthorizedAccessException("Output retention requires a real session.");
        var folder = Path.Combine(_root, session.ToString("N"));
        for (var ancestor = new DirectoryInfo(folder); ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor.LinkTarget is not null) throw new IOException("Linked output directories are forbidden.");
        return folder;
    }
}