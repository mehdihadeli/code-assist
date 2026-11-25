using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace CodeAssist.Models.Catalog;

public enum CatalogRefreshStatus
{
    Updated,
    NotModified,
    Failed,
}

public sealed record CatalogRefreshResult(CatalogRefreshStatus Status, int ModelCount, string? Error);

/// <summary>A cached catalog and an optional warning when the cache could not be used.</summary>
public sealed record CatalogLoad(ModelCatalog Catalog, string? Warning);

/// <summary>
/// Fetches the models.dev feed with ETag revalidation and keeps the last good copy on disk.
/// The caller owns the <see cref="HttpClient"/> and sets its timeout.
/// </summary>
public sealed class ModelCatalogClient(HttpClient http)
{
    public const string FeedFileName = "catalog.json";
    public const string ETagFileName = "catalog.etag";

    private const long MaxFeedBytes = 32L * 1024 * 1024;

    /// <summary>Reads the cached feed. Returns an empty catalog when no usable cache exists.</summary>
    public static CatalogLoad LoadCache(string cacheDirectory)
    {
        var file = Path.Combine(cacheDirectory, FeedFileName);
        if (!File.Exists(file))
        {
            return new CatalogLoad(ModelCatalog.Empty, null);
        }

        try
        {
            return new CatalogLoad(ModelCatalog.Parse(JsonNode.Parse(File.ReadAllText(file))), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException)
        {
            return new CatalogLoad(ModelCatalog.Empty, $"cached catalog is unreadable: {ex.Message}");
        }
    }

    /// <summary>
    /// Revalidates the feed at <paramref name="url"/>. On success the cache is replaced atomically.
    /// On failure the previous cache is left untouched.
    /// </summary>
    public async Task<CatalogRefreshResult> RefreshAsync(string url, string cacheDirectory, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0)
        {
            return Failed("catalog url must be an absolute https URL without embedded credentials");
        }

        var feedFile = Path.Combine(cacheDirectory, FeedFileName);
        var etagFile = Path.Combine(cacheDirectory, ETagFileName);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (File.Exists(etagFile) && LoadCache(cacheDirectory).Catalog.Count > 0)
        {
            var etag = (await File.ReadAllTextAsync(etagFile, cancellationToken)).Trim();
            if (etag.Length > 0 && EntityTagHeaderValue.TryParse(etag, out var tag))
            {
                request.Headers.IfNoneMatch.Add(tag);
            }
        }

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new CatalogRefreshResult(CatalogRefreshStatus.NotModified, LoadCache(cacheDirectory).Catalog.Count, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Failed($"catalog request returned HTTP {(int)response.StatusCode}");
            }

            if (response.Content.Headers.ContentLength > MaxFeedBytes)
            {
                return Failed("catalog feed exceeds the size limit");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaxFeedBytes) return Failed("catalog feed exceeds the size limit");
                buffer.Write(chunk, 0, read);
            }
            var bytes = buffer.ToArray();

            ModelCatalog parsed;
            try
            {
                parsed = ModelCatalog.Parse(JsonNode.Parse(bytes));
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
            {
                return Failed($"catalog feed is not valid: {ex.Message}");
            }

            if (parsed.Count == 0)
            {
                return Failed("catalog feed contains no models");
            }

            Directory.CreateDirectory(cacheDirectory);
            await WriteAtomicAsync(feedFile, bytes, cancellationToken);

            var newEtag = response.Headers.ETag?.ToString();
            if (!string.IsNullOrEmpty(newEtag))
            {
                await WriteAtomicAsync(etagFile, System.Text.Encoding.UTF8.GetBytes(newEtag), cancellationToken);
            }
            else if (File.Exists(etagFile))
            {
                File.Delete(etagFile);
            }

            return new CatalogRefreshResult(CatalogRefreshStatus.Updated, parsed.Count, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed("catalog request timed out");
        }
        catch (HttpRequestException ex)
        {
            return Failed($"catalog request failed: {ex.Message}");
        }
    }

    private static CatalogRefreshResult Failed(string error) =>
        new(CatalogRefreshStatus.Failed, 0, error);

    private static async Task WriteAtomicAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, content, cancellationToken);
        File.Move(temp, path, overwrite: true);
    }
}
