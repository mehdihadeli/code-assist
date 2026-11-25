using System.Security.Cryptography;
using System.Text.Json;

namespace CodeAssist.Persistence;

public sealed class FileConversationStore : IConversationStore, IDisposable
{
    private const int MaxBytes = 8 * 1024 * 1024;
    private readonly string _directory;
    private readonly FileStream _lease;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private sealed record Envelope(int Format, ConversationCheckpoint Value, string Digest);

    public FileConversationStore(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        _lease = new(Path.Combine(_directory, ".owner"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public async Task<IReadOnlyList<ConversationCheckpoint>> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var result = new List<ConversationCheckpoint>();
            foreach (var path in Directory.EnumerateFiles(_directory, "*.json").Order(StringComparer.Ordinal))
            {
                if (result.Count >= 10000) throw new IOException("Stored conversation capacity exceeded.");
                var checkpoint = await ReadFileAsync(path, cancellationToken);
                if (path != Latest(checkpoint.Id)) throw new IOException("Conversation identity does not match its file.");
                result.Add(checkpoint);
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<ConversationCheckpoint> CommitAsync(ConversationCheckpoint checkpoint, long expectedRevision, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var latest = Latest(checkpoint.Id);
            var actual = File.Exists(latest) ? (await ReadLatestAsync(checkpoint.Id, cancellationToken)).Revision : 0;
            if (actual != expectedRevision) throw new JournalConflictException();
            var assigned = checkpoint with { Revision = checked(actual + 1) };
            var value = JsonSerializer.SerializeToUtf8Bytes(assigned, Json);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, assigned, Convert.ToHexString(SHA256.HashData(value))), Json);
            if (bytes.Length > MaxBytes) throw new IOException("Conversation checkpoint exceeds 8 MiB.");
            if (Directory.EnumerateFiles(_directory, "*.json", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) + bytes.Length * 2L > 256L * 1024 * 1024)
                throw new IOException("Conversation storage exceeds 256 MiB.");
            cancellationToken.ThrowIfCancellationRequested();
            await WriteAtomicAsync(History(assigned.Id, assigned.Revision), bytes);
            await WriteAtomicAsync(latest, bytes);
            foreach (var stale in Directory.EnumerateFiles(Folder(assigned.Id), "*.json").OrderDescending(StringComparer.Ordinal).Skip(12))
                try { File.Delete(stale); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return assigned;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<long>> RevisionsAsync(Guid id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = await ReadLatestAsync(id, cancellationToken);
            return Directory.EnumerateFiles(Folder(id), "*.json").Select(Path.GetFileNameWithoutExtension)
                .Select(name => long.TryParse(name, out var revision) ? revision : 0)
                .Where(revision => revision > 0 && revision <= current.Revision).OrderDescending().ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<ConversationCheckpoint> ReadAsync(Guid id, long revision, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = await ReadLatestAsync(id, cancellationToken);
            if (revision < 1 || revision > current.Revision) throw new KeyNotFoundException("Checkpoint not found.");
            var checkpoint = await ReadFileAsync(History(id, revision), cancellationToken);
            if (checkpoint.Id != id || checkpoint.Revision != revision) throw new IOException("Checkpoint identity is invalid.");
            return checkpoint;
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(Guid id, long expectedRevision, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if ((await ReadLatestAsync(id, cancellationToken)).Revision != expectedRevision) throw new JournalConflictException();
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(Latest(id));
            try { Directory.Delete(Folder(id), recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        finally { _gate.Release(); }
    }

    private async Task<ConversationCheckpoint> ReadLatestAsync(Guid id, CancellationToken cancellationToken)
    {
        var checkpoint = await ReadFileAsync(Latest(id), cancellationToken);
        return checkpoint.Id == id ? checkpoint : throw new IOException("Conversation identity does not match its file.");
    }

    private static async Task<ConversationCheckpoint> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new KeyNotFoundException("Conversation checkpoint not found.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxBytes) throw new IOException("Conversation checkpoint exceeds 8 MiB.");
        try
        {
            var envelope = await JsonSerializer.DeserializeAsync<Envelope>(stream, Json, cancellationToken) ?? throw new JsonException();
            if (envelope.Format != 1 || envelope.Value is null || envelope.Value.Id == Guid.Empty || envelope.Value.Revision < 1 ||
                envelope.Digest != Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(envelope.Value, Json))))
                throw new IOException("Conversation integrity check failed.");
            return envelope.Value;
        }
        catch (JsonException exception) { throw new IOException("Conversation checkpoint is invalid.", exception); }
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, CancellationToken.None);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private string Latest(Guid id) => Path.Combine(_directory, Name(id) + ".json");
    private string Folder(Guid id) => Path.Combine(_directory, Name(id));
    private string History(Guid id, long revision) => Path.Combine(Folder(id), revision.ToString("D20") + ".json");
    private static string Name(Guid id) => id != Guid.Empty ? id.ToString("N") : throw new ArgumentException("Session ID is required.", nameof(id));
    public void Dispose() { _lease.Dispose(); _gate.Dispose(); }
}