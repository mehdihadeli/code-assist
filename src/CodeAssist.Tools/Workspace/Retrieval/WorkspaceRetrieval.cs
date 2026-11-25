using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CodeAssist.Tools;
using CodeAssist.Workspaces.Platform;

namespace CodeAssist.Tools;

public sealed class WorkspaceRetrieval(ITextEmbeddings? embeddings = null, IFileSystem? fileSystem = null) : IWorkspaceRetrieval
{
    private readonly IFileSystem _files = fileSystem ?? new LocalFileSystem();
    public bool SupportsSemantic => embeddings is not null;
    public string? EmbeddingDestination => embeddings?.Destination;

    public async Task<RetrievalResult> SearchAsync(string query, string strategy, string path, int limit, ToolContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2000 || limit is < 1 or > 20 || strategy is not ("lexical" or "semantic")) throw new ArgumentException("Invalid retrieval query, strategy or limit.");
        if (strategy == "semantic" && embeddings is null) throw new InvalidOperationException("No trusted embedding generator is configured; lexical retrieval is available.");
        var guard = new PathGuard(context.Workspace, [.. context.ProtectedGlobs, .. context.IgnoreGlobs]);
        var root = guard.Resolve(path);
        if (root.Outside || root.Protected) throw new UnauthorizedAccessException("Retrieval scope is protected or outside the workspace.");
        var rootFacts = _files.Stat(root.Full) ?? throw new IOException("Retrieval scope does not exist.");
        var chunks = new List<RetrievalMatch>();
        var scanned = 0;
        var skipped = 0;
        var complete = true;
        var remaining = 128000;
        var tokens = Regex.Split(query, @"[^\p{L}\p{N}_]+", RegexOptions.CultureInvariant).Where(term => term.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToArray();
        var directories = 0;
        bool Descend(string directory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++directories > 1000) { complete = false; return false; }
            try
            {
                var resolved = guard.Resolve(directory);
                var probe = guard.Resolve(Path.Combine(directory, "scope-probe"));
                return !resolved.Outside && !resolved.Protected && !probe.Outside && !probe.Protected;
            }
            catch (PathRejectedException) { return false; }
        }
        foreach (var entry in rootFacts.IsDirectory ? _files.EnumerateFiles(root.Full, Descend) : [root.Full])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scanned >= Math.Min(context.Limits.MaxSearchFiles, 1000) || chunks.Count >= 128 || remaining <= 0) { complete = false; break; }
            scanned++;
            try
            {
                var resolved = guard.Resolve(entry);
                if (resolved.Outside || resolved.Protected || _files.Stat(resolved.Full) is not { IsDirectory: false } facts || facts.Length > Math.Min(context.Limits.MaxFileBytes, 128000)) { skipped++; continue; }
                await using var stream = _files.OpenRead(resolved.Full);
                var bytes = await LimitedStreamReader.ReadAsync(stream, Math.Min(context.Limits.MaxFileBytes, 128000), cancellationToken);
                if (bytes.Contains((byte)0)) { skipped++; continue; }
                var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
                var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                for (var start = 0; start < lines.Length; start += 24)
                {
                    var body = string.Join('\n', lines.Skip(start).Take(24));
                    if (body.Length > 2000) { skipped++; complete = false; continue; }
                    if (chunks.Count >= 128 || body.Length > remaining) { complete = false; break; }
                    remaining -= body.Length;
                    var searchable = resolved.Relative + "\n" + body;
                    var score = tokens.Count(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase)) / (double)Math.Max(tokens.Length, 1);
                    chunks.Add(new(resolved.Relative, start + 1, Math.Min(start + 24, lines.Length), hash, score, body));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException or PathRejectedException) { skipped++; }
        }
        if (strategy == "semantic" && chunks.Count > 0)
        {
            var vectors = await embeddings!.GenerateAsync([query, .. chunks.Select(chunk => chunk.Path + "\n" + chunk.Text)], cancellationToken);
            if (vectors.Count != chunks.Count + 1 || vectors[0].Length is < 1 or > 16384 || vectors.Any(vector => vector.Length != vectors[0].Length || vector.Any(value => !float.IsFinite(value)))) throw new IOException("Embedding provider returned invalid vectors.");
            for (var index = 0; index < chunks.Count; index++) chunks[index] = chunks[index] with { Score = Cosine(vectors[0], vectors[index + 1]) };
        }
        var matches = new List<RetrievalMatch>();
        foreach (var chunk in chunks.Where(chunk => strategy == "semantic" || chunk.Score > 0).OrderByDescending(chunk => chunk.Score).ThenBy(chunk => chunk.Path, StringComparer.Ordinal).ThenBy(chunk => chunk.StartLine))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var current = guard.Resolve(chunk.Path);
                if (current.Outside || current.Protected) continue;
                await using var stream = _files.OpenRead(current.Full);
                var bytes = await LimitedStreamReader.ReadAsync(stream, Math.Min(context.Limits.MaxFileBytes, 128000), cancellationToken);
                if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != chunk.Sha256) { complete = false; continue; }
                matches.Add(chunk);
                if (matches.Count == limit) break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PathRejectedException) { complete = false; }
        }
        return new(strategy, strategy == "semantic" ? embeddings!.Name : null, complete, scanned, skipped, matches);
    }

    private static double Cosine(float[] first, float[] second)
    {
        double product = 0, firstNorm = 0, secondNorm = 0;
        for (var index = 0; index < first.Length; index++)
        {
            product += (double)first[index] * second[index];
            firstNorm += (double)first[index] * first[index];
            secondNorm += (double)second[index] * second[index];
        }
        if (firstNorm == 0 || secondNorm == 0) throw new IOException("Embedding provider returned a zero vector.");
        return Math.Clamp(product / Math.Sqrt(firstNorm * secondNorm), -1, 1);
    }
}