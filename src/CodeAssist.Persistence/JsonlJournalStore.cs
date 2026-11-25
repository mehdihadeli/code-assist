using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeAssist.Contracts;

namespace CodeAssist.Persistence;

public sealed class JsonlJournalStore(string directory) : IJournalStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaxJournalBytes = 16 * 1024 * 1024;
    private sealed record Envelope(int Version, JsonElement Entry, string Hash);

    public async Task<IReadOnlyList<JournalEntry>> ReadAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var path = PathFor(sessionId);
        var gate = Gates.GetOrAdd(path, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { return await ReadFileAsync(path, sessionId, cancellationToken); }
        finally { gate.Release(); }
    }

    public async Task<JournalEntry> AppendAsync(JournalEntry entry, long expectedSequence, CancellationToken cancellationToken)
    {
        var path = PathFor(entry.SessionId);
        var gate = Gates.GetOrAdd(path, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            var entries = await ReadStreamAsync(stream, entry.SessionId, cancellationToken);
            var sequence = entries.LastOrDefault()?.Seq ?? 0;
            if (sequence != expectedSequence) throw new JournalConflictException();
            var assigned = entry with { Seq = sequence + 1, At = DateTimeOffset.UtcNow };
            var value = JsonSerializer.SerializeToElement<JournalEntry>(assigned, Json);
            var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes<JournalEntry>(assigned, Json)));
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Envelope(1, value, hash), Json) + "\n");
            if (bytes.Length > 1_048_576 || stream.Length + bytes.Length > MaxJournalBytes) throw new IOException("Journal size limit reached; segment rotation is required.");
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = stream.Length;
            await stream.WriteAsync(bytes, CancellationToken.None);
            await stream.FlushAsync(CancellationToken.None);
            stream.Flush(flushToDisk: true);
            return assigned;
        }
        finally { gate.Release(); }
    }

    private async Task<IReadOnlyList<JournalEntry>> ReadFileAsync(string path, Guid sessionId, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return [];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
        return await ReadStreamAsync(stream, sessionId, cancellationToken);
    }

    private static async Task<IReadOnlyList<JournalEntry>> ReadStreamAsync(FileStream stream, Guid sessionId, CancellationToken cancellationToken)
    {
        if (stream.Length > MaxJournalBytes) throw new IOException("Journal exceeds supported segment size.");
        var bytes = new byte[stream.Length];
        stream.Position = 0;
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (bytes.Length > 0 && bytes[^1] != '\n') throw new IOException("Journal has an incomplete tail; recovery is required before writing.");
        var entries = new List<JournalEntry>();
        foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var envelope = JsonSerializer.Deserialize<Envelope>(line, Json) ?? throw new JsonException();
                var entry = envelope.Entry.Deserialize<JournalEntry>(Json) ?? throw new JsonException();
                var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes<JournalEntry>(entry, Json)));
                if (envelope.Version != 1 || hash != envelope.Hash) throw new IOException("Journal checksum or envelope version is invalid.");
                if (entry.SessionId != sessionId || entry.Seq != entries.Count + 1L) throw new IOException("Journal session or sequence is invalid.");
                entries.Add(entry);
            }
            catch (JsonException exception) { throw new IOException("Journal record is invalid.", exception); }
        }
        return entries;
    }

    private string PathFor(Guid sessionId)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("A real session ID is required.", nameof(sessionId));
        return Path.GetFullPath(Path.Combine(directory, sessionId.ToString("N") + ".jsonl"));
    }
}