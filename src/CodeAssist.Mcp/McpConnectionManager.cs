using CodeAssist.Tools;
using CodeAssist.Workspaces.Platform;

namespace CodeAssist.Mcp;

public sealed class McpConnectionManager : IMcpConnections, IMcpPrompts, IResourceSource, IAsyncDisposable
{
    private sealed class Entry
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal McpConnectionLease? Lease;
        internal IReadOnlyList<ITool> Snapshot = [];
        internal volatile bool Retired;
    }

    private readonly Dictionary<string, McpServerProfile> _profiles;
    private readonly Dictionary<(Guid Session, string Server), Entry> _entries = [];
    private volatile bool _disposed;
    public IReadOnlyList<string> Servers => _profiles.Keys.Order(StringComparer.Ordinal).ToArray();
    public IReadOnlyList<ToolEffect> ConnectEffects(string server) => _profiles.TryGetValue(server, out var profile)
        ? profile.StartupEffects?.ToArray() ?? [new(ToolEffectKind.Net, $"mcp:{server}")]
        : throw new ArgumentException("Unknown MCP server.");

    public McpConnectionManager(IEnumerable<McpServerProfile> profiles)
    {
        var items = profiles.Take(65).ToArray();
        if (items.Length > 64 || items.Any(profile => string.IsNullOrEmpty(profile.Id) || profile.Id.Length > 80 ||
            profile.Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_') ||
            profile.Allow.Count > 256 || profile.Deny.Count > 256 || profile.ToolOverrides.Count > 256 || profile.Allow.Concat(profile.Deny).Any(pattern => string.IsNullOrWhiteSpace(pattern) || pattern.Length > 256))) throw new ArgumentException("Configure at most 64 bounded named MCP profiles.");
        _profiles = items.ToDictionary(profile => profile.Id, profile => profile with { Allow = profile.Allow.ToArray(), Deny = profile.Deny.ToArray(), StartupEffects = profile.StartupEffects?.ToArray(), ToolOverrides = new Dictionary<string, bool>(profile.ToolOverrides, StringComparer.Ordinal) }, StringComparer.Ordinal);
    }

    public async Task<McpConnectionStatus> StatusAsync(string server, ToolContext context, CancellationToken cancellationToken)
    {
        var entry = Find(server, context, create: false);
        if (entry is null) return new(server, false, 0);
        await entry.Gate.WaitAsync(cancellationToken);
        try { return new(server, entry.Lease?.Connection.IsConnected == true, entry.Snapshot.Count); }
        finally { entry.Gate.Release(); }
    }

    public async Task<McpConnectionStatus> ConnectAsync(string server, ToolContext context, CancellationToken cancellationToken)
    {
        var entry = Require(server, context);
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed || entry.Retired, this);
            if (entry.Lease?.Connection.IsConnected == true) return new(server, true, entry.Snapshot.Count);
            if (entry.Lease is not null) { await entry.Lease.DisposeAsync(); entry.Lease = null; }
            try
            {
                entry.Lease = await _profiles[server].Open(context, cancellationToken);
                if (entry.Retired) throw new InvalidOperationException("MCP session was released during connection startup.");
                entry.Snapshot = Select(server, entry.Lease.Connection.Tools);
                return new(server, true, entry.Snapshot.Count);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                if (entry.Lease is not null) { await entry.Lease.DisposeAsync(); entry.Lease = null; }
                entry.Snapshot = [];
                throw new IOException($"MCP connection failed ({exception.GetType().Name}); inspect trusted host diagnostics.", exception);
            }
        }
        finally { entry.Gate.Release(); }
    }

    public async Task DisconnectAsync(string server, ToolContext context, CancellationToken cancellationToken)
    {
        var entry = Find(server, context, create: false);
        if (entry is null) return;
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            var lease = entry.Lease;
            entry.Lease = null;
            entry.Snapshot = [];
            if (lease is not null) await lease.DisposeAsync();
        }
        finally { entry.Gate.Release(); }
    }

    public async Task ForgetCredentialsAsync(string server, ToolContext context, CancellationToken cancellationToken)
    {
        _ = ConnectEffects(server);
        await DisconnectAsync(server, context, cancellationToken);
        if (_profiles[server].ForgetCredentials is { } forget) await forget(context, cancellationToken);
    }

    public async Task ReleaseSessionAsync(ToolContext context, CancellationToken cancellationToken)
    {
        Entry[] retired;
        lock (_entries)
        {
            var keys = _entries.Keys.Where(key => key.Session == context.SessionId).ToArray();
            retired = keys.Select(key => _entries[key]).ToArray();
            foreach (var key in keys) { _entries[key].Retired = true; _entries.Remove(key); }
        }
        foreach (var entry in retired)
        {
            await entry.Gate.WaitAsync(CancellationToken.None);
            try { if (entry.Lease is not null) await entry.Lease.DisposeAsync(); entry.Lease = null; entry.Snapshot = []; }
            finally { entry.Gate.Release(); }
        }
    }

    public async Task<IReadOnlyList<ToolSpec>> ListAsync(string server, ToolContext context, CancellationToken cancellationToken)
    {
        var entry = Find(server, context, create: false) ?? throw new InvalidOperationException("Connect this MCP server in the current session first.");
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            var connection = Connected(entry);
            if (!_profiles[server].FreezeCatalog)
            {
                await connection.RefreshAsync(cancellationToken);
                entry.Snapshot = Select(server, connection.Tools);
            }
            return entry.Snapshot.Select(tool => tool.Spec).ToArray();
        }
        finally { entry.Gate.Release(); }
    }

    public async Task<ToolResult> CallAsync(string server, string tool, ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var entry = Find(server, context, create: false) ?? throw new InvalidOperationException("Connect this MCP server in the current session first.");
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            _ = Connected(entry);
            var catalog = new ToolCatalog(entry.Snapshot);
            var selected = catalog.Find($"mcp.{server}.{tool}") ?? throw new UnauthorizedAccessException("MCP tool is not admitted by this connection profile.");
            catalog.ValidateArguments(selected, arguments);
            return await selected.ExecuteAsync(arguments, context, cancellationToken);
        }
        finally { entry.Gate.Release(); }
    }

    Task<ResourceCatalog> IResourceSource.ListAsync(string server, ToolContext context, CancellationToken cancellationToken) =>
        WithConnection(server, context, cancellationToken, connection => connection.ListAsync(server, context, cancellationToken));

    public Task<IReadOnlyList<ResourceContent>?> ReadAsync(string server, string uri, ToolContext context, CancellationToken cancellationToken) =>
        WithConnection(server, context, cancellationToken, connection => connection.ReadAsync(server, uri, context, cancellationToken));

    public Task<IReadOnlyList<McpPromptEntry>> ListPromptsAsync(string server, ToolContext context, CancellationToken cancellationToken) =>
        WithConnection(server, context, cancellationToken, connection => connection.ListPromptsAsync(server, context, cancellationToken));

    public Task<McpPromptResult> GetPromptAsync(string server, string name, IReadOnlyDictionary<string, string> arguments, ToolContext context, CancellationToken cancellationToken) =>
        WithConnection(server, context, cancellationToken, connection => connection.GetPromptAsync(server, name, arguments, context, cancellationToken));

    private async Task<TResult> WithConnection<TResult>(string server, ToolContext context, CancellationToken cancellationToken, Func<McpToolConnection, Task<TResult>> action)
    {
        var entry = Find(server, context, create: false) ?? throw new InvalidOperationException("Connect this MCP server in the current session first.");
        await entry.Gate.WaitAsync(cancellationToken);
        try { return await action(Connected(entry)); }
        finally { entry.Gate.Release(); }
    }

    private IReadOnlyList<ITool> Select(string server, IReadOnlyList<ITool> tools)
    {
        var profile = _profiles[server];
        var allow = new GlobMatcher(profile.Allow);
        var deny = new GlobMatcher(profile.Deny);
        var prefix = $"mcp.{server}.";
        return tools.Where(tool => profile.ToolOverrides.GetValueOrDefault(tool.Spec.Name[prefix.Length..], profile.ToolsEnabled) &&
            (profile.Allow.Count == 0 || allow.IsMatch(tool.Spec.Name[prefix.Length..])) && !deny.IsMatch(tool.Spec.Name[prefix.Length..])).ToArray();
    }

    private Entry Require(string server, ToolContext context) => Find(server, context, create: true)!;

    private Entry? Find(string server, ToolContext context, bool create)
    {
        lock (_entries)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (context.SessionId == Guid.Empty) throw new UnauthorizedAccessException("MCP connections require a real session ID.");
            if (!_profiles.ContainsKey(server)) throw new ArgumentException("Unknown or disabled MCP server.");
            var key = (context.SessionId, server);
            if (_entries.TryGetValue(key, out var entry)) return entry;
            if (!create) return null;
            if (_entries.Count >= 128) throw new InvalidOperationException("MCP session-connection budget reached.");
            return _entries[key] = new Entry();
        }
    }

    private static McpToolConnection Connected(Entry entry) => entry.Lease?.Connection is { IsConnected: true } connection ? connection :
        throw new InvalidOperationException("Connect this MCP server in the current session first.");

    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        lock (_entries)
        {
            if (_disposed) return;
            _disposed = true;
            entries = _entries.Values.ToArray();
        }
        foreach (var entry in entries)
        {
            await entry.Gate.WaitAsync();
            try { if (entry.Lease is not null) await entry.Lease.DisposeAsync(); entry.Lease = null; entry.Snapshot = []; }
            finally { entry.Gate.Release(); }
        }
    }
}