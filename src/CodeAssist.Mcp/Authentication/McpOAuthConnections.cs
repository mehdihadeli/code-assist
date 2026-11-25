using CodeAssist.Tools;
using CodeAssist.Workspaces.Platform;
using ModelContextProtocol.Client;

namespace CodeAssist.Mcp;

public sealed class McpOAuthConnections : IMcpConnections, IAsyncDisposable
{
    private readonly Dictionary<string, AuthenticatedMcpServer> _servers;
    private readonly Dictionary<(Guid Session, string Server), AuthenticatedMcpSession> _sessions = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _allowPrivate;
    private bool _disposed;
    public IReadOnlyList<string> Servers => _servers.Keys.Order(StringComparer.Ordinal).ToArray();

    public McpOAuthConnections(IEnumerable<AuthenticatedMcpServer> servers, bool allowPrivateHosts = false)
    {
        _allowPrivate = allowPrivateHosts;
        _servers = servers.ToDictionary(server => server.Id, StringComparer.Ordinal);
        if (_servers.Count > 64 || _servers.Values.Any(server => string.IsNullOrWhiteSpace(server.Id) || server.Id.Length > 80 ||
            server.Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_') || !server.Endpoint.IsAbsoluteUri ||
            server.Endpoint.UserInfo.Length != 0 || server.Endpoint.Fragment.Length != 0 || server.Endpoint.Query.Length != 0 ||
            server.Endpoint.Scheme != "https" && !(allowPrivateHosts && server.Endpoint.Scheme == "http")))
            throw new ArgumentException("Configure at most 64 named HTTPS MCP servers without embedded credentials.");
    }

    public async Task<McpConnectionStatus> StatusAsync(string server, ToolContext context, CancellationToken cancellationToken)
    {
        Require(server, context);
        await _gate.WaitAsync(cancellationToken);
        try { return new(server, _sessions.TryGetValue((context.SessionId, server), out var session) && session.Connection.IsConnected, session?.Connection.Tools.Count ?? 0); }
        finally { _gate.Release(); }
    }

    public async Task<McpConnectionStatus> ConnectAsync(string server, ToolContext context, CancellationToken cancellationToken)
    {
        var configuration = Require(server, context);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(context.Limits.ProcessTimeout);
        await _gate.WaitAsync(deadline.Token);
        try
        {
            if (_sessions.TryGetValue((context.SessionId, server), out var existing))
            {
                if (existing.Connection.IsConnected) return new(server, true, existing.Connection.Tools.Count);
                _sessions.Remove((context.SessionId, server));
                await existing.DisposeAsync();
            }
            if (_sessions.Count >= 128) throw new InvalidOperationException("MCP connection budget reached.");
            var oauth = configuration.OAuth(context);
            if (oauth.AuthorizationCallbackHandler is null) throw new ArgumentException("OAuth requires a trusted state-aware authorization callback.");
            var redirect = oauth.RedirectUri;
            if (!redirect.IsAbsoluteUri || redirect.UserInfo.Length != 0 || redirect.Query.Length != 0 || redirect.Fragment.Length != 0 ||
                redirect.Scheme != "https" && !(redirect.Scheme == "http" && redirect.IsLoopback)) throw new ArgumentException("OAuth callback must use HTTPS or loopback HTTP.");
            var http = new HttpClient(NetworkGuard.CreateHandler(_allowPrivate)) { Timeout = Timeout.InfiniteTimeSpan };
            try
            {
                var transport = new HttpClientTransport(new() { Endpoint = configuration.Endpoint, Name = server, OAuth = oauth }, http);
                var connection = await McpToolConnection.ConnectAsync(server, transport, deadline.Token);
                _sessions.Add((context.SessionId, server), new(connection, http));
                return new(server, true, connection.Tools.Count);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                http.Dispose();
                throw new IOException("MCP authentication or connection failed; inspect trusted host diagnostics.");
            }
            catch { http.Dispose(); throw; }
        }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync(string server, ToolContext context, CancellationToken cancellationToken)
    {
        Require(server, context);
        await _gate.WaitAsync(cancellationToken);
        try { if (_sessions.Remove((context.SessionId, server), out var session)) await session.DisposeAsync(); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ToolSpec>> ListAsync(string server, ToolContext context, CancellationToken cancellationToken)
    {
        Require(server, context);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var connection = Connected(server, context).Connection;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(context.Limits.ProcessTimeout);
            await connection.RefreshAsync(timeout.Token);
            return connection.Tools.Select(tool => tool.Spec).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<ToolResult> CallAsync(string server, string tool, ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        Require(server, context);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var catalog = new ToolCatalog(Connected(server, context).Connection.Tools);
            var entry = catalog.Find($"mcp.{server}.{tool}") ?? throw new ArgumentException("Unknown connected MCP tool.");
            catalog.ValidateArguments(entry, arguments);
            return await entry.ExecuteAsync(arguments, context, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private AuthenticatedMcpSession Connected(string server, ToolContext context) => _sessions.GetValueOrDefault((context.SessionId, server)) is { Connection.IsConnected: true } session ? session :
        throw new InvalidOperationException("Authenticate or reconnect this server in the current session first.");
    private AuthenticatedMcpServer Require(string server, ToolContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (context.SessionId == Guid.Empty) throw new UnauthorizedAccessException("MCP connections require a real session ID.");
        return _servers.GetValueOrDefault(server) ?? throw new ArgumentException("Unknown configured MCP server.");
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var session in _sessions.Values) await session.DisposeAsync();
            _sessions.Clear();
        }
        finally { _gate.Release(); }
    }
}