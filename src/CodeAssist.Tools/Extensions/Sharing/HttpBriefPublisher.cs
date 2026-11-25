using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Workspaces.Platform;

namespace CodeAssist.Tools.Extensions;

public sealed class HttpBriefPublisher : IBriefPublisher, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    public Uri Destination { get; }

    public HttpBriefPublisher(Uri destination, bool allowPrivateHosts = false, HttpClient? http = null)
    {
        if (!destination.IsAbsoluteUri || destination.UserInfo.Length != 0 || destination.Fragment.Length != 0 ||
            destination.Query.Length != 0 || destination.Scheme != "https" && !(allowPrivateHosts && destination.Scheme == "http"))
            throw new ArgumentException("Sharing requires a configured HTTPS endpoint without credentials, query or fragment.");
        Destination = destination;
        _ownsClient = http is null;
        _http = http ?? new HttpClient(NetworkGuard.CreateHandler(allowPrivateHosts)) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<BriefReceipt> PublishAsync(BriefDocument document, ToolContext context, CancellationToken cancellationToken)
    {
        if (context.SessionId == Guid.Empty || string.IsNullOrWhiteSpace(context.CallId)) throw new ArgumentException("Publication requires stable session and call IDs.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(context.Limits.ProcessTimeout);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(JsonSerializer.Serialize(new { document.Title, document.Text }), Encoding.UTF8, "application/json"), "report");
        foreach (var attachment in document.Attachments)
        {
            var content = new ByteArrayContent(attachment.Data);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(content, "attachments", Path.GetFileName(attachment.Name));
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, Destination) { Content = form };
        request.Headers.Add("Idempotency-Key", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{context.SessionId}:{context.CallId}"))));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        var bytes = await LimitedStreamReader.ReadAsync(stream, 65536, deadline.Token);
        using var json = JsonDocument.Parse(bytes);
        var id = json.RootElement.GetProperty("id").GetString();
        var url = json.RootElement.GetProperty("url").GetString();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || !Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https") || address.UserInfo.Length != 0)
            throw new IOException("Sharing server returned an invalid receipt.");
        return new(id, address);
    }

    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}