using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Workspaces.Platform;

namespace CodeAssist.Tools.Extensions;

public sealed class SearxngWebSearch : IWebSearch, IDisposable
{
    private readonly Uri _endpoint;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    public string ProviderId => _endpoint.AbsoluteUri;

    public SearxngWebSearch(Uri endpoint, bool allowPrivateHosts = false, HttpClient? http = null)
    {
        if (endpoint.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment)) throw new ArgumentException("Search endpoint must be HTTP(S), without credentials or fragment.", nameof(endpoint));
        _endpoint = endpoint;
        _http = http ?? new HttpClient(NetworkGuard.CreateHandler(allowPrivateHosts)) { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttp = http is null;
    }

    public async Task<IReadOnlyList<WebSearchHit>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 4000) throw new ArgumentException("Search query must contain 1-4000 characters.", nameof(query));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var uri = new UriBuilder(_endpoint) { Query = "q=" + Uri.EscapeDataString(query) + "&format=json" }.Uri;
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 1_048_576) throw new IOException("Search response exceeds 1 MiB.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (memory.Length + count > 1_048_576) throw new IOException("Search response exceeds 1 MiB.");
            memory.Write(buffer, 0, count);
        }
        using var document = JsonDocument.Parse(memory.ToArray());
        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) throw new IOException("Search response lacks its results array.");
        var hits = new List<WebSearchHit>();
        foreach (var result in results.EnumerateArray())
        {
            if (hits.Count == 20) break;
            if (!result.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String || url.GetString() is not { Length: <= 2048 } text || !Uri.TryCreate(text, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(address.UserInfo)) continue;
            string Value(string key, int limit) => result.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? (value.GetString() ?? "")[..Math.Min(value.GetString()!.Length, limit)] : "";
            hits.Add(new(Value("title", 200), address.AbsoluteUri, Value("content", 2000)));
        }
        return hits;
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}