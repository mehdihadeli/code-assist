namespace CodeAssist.Mcp;

internal sealed class AuthenticatedMcpSession(McpToolConnection connection, HttpClient http) : IAsyncDisposable
{
    public McpToolConnection Connection { get; } = connection;
    public async ValueTask DisposeAsync()
    {
        try { await Connection.DisposeAsync(); }
        finally { http.Dispose(); }
    }
}