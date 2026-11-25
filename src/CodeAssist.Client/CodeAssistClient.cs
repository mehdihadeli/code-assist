using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeAssist.Contracts;

namespace CodeAssist.Client;

public sealed class CodeAssistClient : IDisposable
{
    private readonly HttpClient _http;

    public CodeAssistClient(Uri endpoint, string token)
    {
        if (!endpoint.IsAbsoluteUri || !endpoint.IsLoopback || endpoint.Scheme is not ("http" or "https") ||
            endpoint.UserInfo.Length != 0 || endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("Use a loopback server origin without paths or credentials.", nameof(endpoint));
        if (token.Length is < 32 or > 512 || token.Any(char.IsControl)) throw new ArgumentException("Invalid server token.", nameof(token));
        _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = endpoint, Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task CheckAsync(CancellationToken cancellation = default)
    {
        var health = await GetAsync<ServerHealthDto>("/v1/health", cancellation);
        if (health is not { Application: "code-assist", Protocol: "1", Status: "ready" }) throw new InvalidDataException("Server identity or protocol is incompatible.");
    }

    public Task<AgentSessionDto> CreateAsync(string? model = null, CancellationToken cancellation = default) =>
        PostAsync<AgentSessionDto>("/v1/sessions", new CreateAgentSessionRequest(model), cancellation);

    public Task<AgentSessionDto[]> SessionsAsync(CancellationToken cancellation = default) => GetAsync<AgentSessionDto[]>("/v1/sessions", cancellation);
    public Task<AgentSessionDto> SessionAsync(Guid session, CancellationToken cancellation = default) => GetAsync<AgentSessionDto>($"/v1/sessions/{session}", cancellation);
    public Task<AgentSessionDetails> SessionDetailsAsync(Guid session, CancellationToken cancellation = default) => GetAsync<AgentSessionDetails>($"/v1/sessions/{session}/details", cancellation);
    public Task<AgentSessionDetails> UpdateSessionAsync(Guid session, UpdateSessionRequest request, CancellationToken cancellation = default) => PostAsync<AgentSessionDetails>($"/v1/sessions/{session}/details", request, cancellation);
    public Task<long[]> SessionCheckpointsAsync(Guid session, CancellationToken cancellation = default) => GetAsync<long[]>($"/v1/sessions/{session}/checkpoints", cancellation);
    public Task<AgentSessionDto> RewindSessionAsync(Guid session, RewindSessionRequest request, CancellationToken cancellation = default) => PostAsync<AgentSessionDto>($"/v1/sessions/{session}/rewind", request, cancellation);
    public Task<ConversationCommandDto[]> CommandsAsync(CancellationToken cancellation = default) => GetAsync<ConversationCommandDto[]>("/v1/commands", cancellation);
    public Task<ConversationCommandExpansion> ExpandCommandAsync(ExpandConversationCommandRequest request, CancellationToken cancellation = default) => PostAsync<ConversationCommandExpansion>("/v1/commands/expand", request, cancellation);
    public Task<AgentSessionArchive> ExportSessionAsync(Guid session, CancellationToken cancellation = default) => GetAsync<AgentSessionArchive>($"/v1/sessions/{session}/archive", cancellation);
    public Task<AgentSessionDto> ForkSessionAsync(Guid session, CancellationToken cancellation = default) => PostAsync<AgentSessionDto>($"/v1/sessions/{session}/fork", null, cancellation);
    public Task<AgentSessionDto> ImportSessionAsync(AgentSessionArchive archive, CancellationToken cancellation = default) => PostAsync<AgentSessionDto>("/v1/sessions/import", archive, cancellation);
    public Task<AgentMessageDto[]> MessagesAsync(Guid session, CancellationToken cancellation = default) => GetAsync<AgentMessageDto[]>($"/v1/sessions/{session}/messages", cancellation);
    public Task<CancelAgentTurnResponse> CancelAsync(Guid session, CancellationToken cancellation = default) => PostAsync<CancelAgentTurnResponse>($"/v1/sessions/{session}/cancel", null, cancellation);

    public async Task DeleteSessionAsync(Guid session, CancellationToken cancellation = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await _http.DeleteAsync($"/v1/sessions/{session}", deadline.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Server request failed ({(int)response.StatusCode}).", null, response.StatusCode);
    }

    public async IAsyncEnumerable<AgentTurnEvent> StreamAsync(Guid session, string text, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/sessions/{session}/messages/stream");
        request.Content = JsonContent.Create(new SendAgentMessageRequest(text));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Server request failed ({(int)response.StatusCode}).", null, response.StatusCode);
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream") throw new InvalidDataException("Expected a server event stream.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        var parser = SseParser.Create<AgentTurnEvent>(stream, (_, data) =>
            JsonSerializer.Deserialize<AgentTurnEvent>(data, JsonSerializerOptions.Web) ?? throw new InvalidDataException("Invalid turn event."));
        await foreach (var item in parser.EnumerateAsync(deadline.Token))
        {
            yield return item.Data;
            if (item.Data.Kind is "turn.completed" or "turn.failed" or "turn.canceled") yield break;
        }
        throw new InvalidDataException("Turn stream ended before completion. Read committed messages before retrying.");
    }

    public IReadOnlyList<CatalogProviderDto> ListProviders() => GetAsync<CatalogProviderDto[]>("/v1/catalog/providers").GetAwaiter().GetResult();
    public IReadOnlyList<CatalogModelDto> ListModels(string? providerId = null) => GetAsync<CatalogModelDto[]>("/v1/catalog/models" +
        (providerId is null ? "" : "?providerId=" + Uri.EscapeDataString(providerId))).GetAwaiter().GetResult();
    public Task<CatalogRefreshDto> RefreshCatalogAsync(CancellationToken cancellation) => PostAsync<CatalogRefreshDto>("/v1/catalog/refresh", null, cancellation);
    public IReadOnlyList<ToolInfoDto> ListTools(IReadOnlyCollection<string>? allow = null, IReadOnlyCollection<string>? deny = null) =>
        PostAsync<ToolInfoDto[]>("/v1/tools/query", new { allow, deny }).GetAwaiter().GetResult();
    public ToolInfoDto? GetTool(string name) => ListTools().FirstOrDefault(tool => tool.Name == name);

    public Task<TResult> GetAsync<TResult>(string path, CancellationToken cancellation = default) => SendAsync<TResult>(HttpMethod.Get, path, null, cancellation);
    public Task<TResult> PostAsync<TResult>(string path, object? body, CancellationToken cancellation = default) => SendAsync<TResult>(HttpMethod.Post, path, body, cancellation);

    public async Task<ApiResponse> RequestAsync(string method, string path, JsonElement? body = null, CancellationToken cancellation = default)
    {
        if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS")) throw new ArgumentException("Unsupported HTTP method.", nameof(method));
        if ((!path.StartsWith("/v1/", StringComparison.Ordinal) && path != "/openapi/v1.json") || path.Contains('#') || path.Contains('\\') || path.Any(char.IsControl))
            throw new ArgumentException("Use an API path on the selected service.", nameof(path));
        if (body is not null && method is "GET" or "HEAD") throw new ArgumentException("GET/HEAD do not accept a request body.", nameof(body));
        if (body is { } data && System.Text.Encoding.UTF8.GetByteCount(data.GetRawText()) > 393216) throw new ArgumentException("Request body exceeds 384 KiB.", nameof(body));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is { } payload) request.Content = JsonContent.Create(payload);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Server request failed ({(int)response.StatusCode}).", null, response.StatusCode);
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream") throw new InvalidDataException("Use run/chat for streaming operations.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, deadline.Token)) != 0)
        {
            if (buffer.Length + count > 8 * 1024 * 1024) throw new InvalidDataException("Server response exceeds its budget.");
            buffer.Write(chunk, 0, count);
        }
        return new((int)response.StatusCode, response.Content.Headers.ContentType?.MediaType,
            new System.Text.UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length)));
    }

    private async Task<TResult> SendAsync<TResult>(HttpMethod method, string path, object? body, CancellationToken cancellation)
    {
        if (!path.StartsWith("/v1/", StringComparison.Ordinal) || path.Contains('#')) throw new ArgumentException("Invalid API path.", nameof(path));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Server request failed ({(int)response.StatusCode}).", null, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, deadline.Token)) != 0)
        {
            if (buffer.Length + count > 8 * 1024 * 1024) throw new InvalidDataException("Server response exceeds its budget.");
            buffer.Write(chunk, 0, count);
        }
        return JsonSerializer.Deserialize<TResult>(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Invalid server response.");
    }

    public void Dispose() => _http.Dispose();
}