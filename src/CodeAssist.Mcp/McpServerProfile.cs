using CodeAssist.Tools;

namespace CodeAssist.Mcp;

public sealed record McpServerProfile(string Id, Func<ToolContext, CancellationToken, Task<McpConnectionLease>> Open)
{
    public IReadOnlyList<string> Allow { get; init; } = [];
    public IReadOnlyList<string> Deny { get; init; } = [];
    public bool FreezeCatalog { get; init; }
    public IReadOnlyList<ToolEffect>? StartupEffects { get; init; }
    public Func<ToolContext, CancellationToken, Task>? ForgetCredentials { get; init; }
    public bool ToolsEnabled { get; init; } = true;
    public IReadOnlyDictionary<string, bool> ToolOverrides { get; init; } = new Dictionary<string, bool>();
}

public sealed class McpConnectionLease(McpToolConnection connection, IDisposable? owner = null) : IAsyncDisposable
{
    public McpToolConnection Connection { get; } = connection;
    public async ValueTask DisposeAsync()
    {
        try { await Connection.DisposeAsync(); }
        finally { owner?.Dispose(); }
    }
}