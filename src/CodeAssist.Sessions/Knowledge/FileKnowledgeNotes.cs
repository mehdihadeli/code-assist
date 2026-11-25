using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeAssist.Tools;

namespace CodeAssist.Sessions;

public sealed class FileKnowledgeNotes(string directory) : IKnowledgeNotes
{
    private readonly string _root = Path.GetFullPath(directory);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<KnowledgeNote>> ListAsync(string scope, ToolContext context, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await LoadAsync(Bucket(scope, context), cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task<KnowledgeNote> PutAsync(string scope, Guid? id, long revision, string title, string body, ToolContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 120 || body.Length > 8000 || title.Contains('\0') || body.Contains('\0')) throw new ArgumentException("Note title/body exceeds limits or contains NUL.");
        return await MutateAsync(scope, context, notes =>
        {
            var previous = id is { } key ? notes.SingleOrDefault(note => note.Id == key) : null;
            if (id is not null && previous is null || revision != (previous?.Revision ?? 0)) throw new InvalidOperationException("Note revision conflict or unavailable note.");
            if (previous is null && notes.Count >= 100) throw new IOException("Scope note capacity reached.");
            var next = new KnowledgeNote(previous?.Id ?? Guid.NewGuid(), checked(revision + 1), title, body, DateTimeOffset.UtcNow);
            if (previous is not null) notes.Remove(previous);
            notes.Add(next);
            return next;
        }, cancellationToken);
    }

    public async Task RemoveAsync(string scope, Guid id, long revision, ToolContext context, CancellationToken cancellationToken)
    {
        await MutateAsync(scope, context, notes =>
        {
            var previous = notes.SingleOrDefault(note => note.Id == id);
            if (previous is null || previous.Revision != revision) throw new InvalidOperationException("Note revision conflict or unavailable note.");
            notes.Remove(previous);
            return true;
        }, cancellationToken);
    }

    private async Task<T> MutateAsync<T>(string scope, ToolContext context, Func<List<KnowledgeNote>, T> mutate, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var folder = Bucket(scope, context);
            Directory.CreateDirectory(folder);
            var lockPath = Path.Combine(folder, "write.lock");
            if (new FileInfo(lockPath).LinkTarget is not null) throw new IOException("Linked knowledge files are forbidden.");
            await using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var notes = (await LoadAsync(folder, cancellationToken)).ToList();
            var result = mutate(notes);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(notes.OrderBy(note => note.Id));
            if (bytes.Length > 2 * 1024 * 1024) throw new IOException("Knowledge scope storage ceiling reached.");
            var temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
                File.Move(temporary, Path.Combine(folder, "notes.json"), true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return result;
        }
        finally { _gate.Release(); }
    }

    private static async Task<IReadOnlyList<KnowledgeNote>> LoadAsync(string folder, CancellationToken cancellationToken)
    {
        var path = Path.Combine(folder, "notes.json");
        var info = new FileInfo(path);
        if (info.LinkTarget is not null) throw new IOException("Linked knowledge files are forbidden.");
        if (!info.Exists) return [];
        if (info.Length > 2 * 1024 * 1024) throw new IOException("Knowledge scope exceeds limits.");
        var notes = JsonSerializer.Deserialize<List<KnowledgeNote>>(await File.ReadAllBytesAsync(path, cancellationToken)) ?? throw new IOException("Invalid knowledge scope.");
        if (notes.Count > 100 || notes.Select(note => note.Id).Distinct().Count() != notes.Count || notes.Any(note => note.Id == Guid.Empty || note.Revision < 1 || note.Title.Length > 120 || note.Body.Length > 8000)) throw new IOException("Invalid knowledge entries.");
        return notes;
    }

    private string Bucket(string scope, ToolContext context)
    {
        var workspace = Path.GetFullPath(context.Workspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) workspace = workspace.ToUpperInvariant();
        var key = scope switch
        {
            "session" when context.SessionId != Guid.Empty => context.SessionId.ToString("N"),
            "workspace" => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(workspace))),
            "user" => "local-user",
            _ => throw new UnauthorizedAccessException("Invalid knowledge scope or session."),
        };
        var path = Path.Combine(_root, scope, key);
        for (var ancestor = new DirectoryInfo(path); ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor.LinkTarget is not null) throw new IOException("Linked knowledge directories are forbidden.");
        return path;
    }
}