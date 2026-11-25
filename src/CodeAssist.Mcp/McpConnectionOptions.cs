using ModelContextProtocol.Client;

namespace CodeAssist.Mcp;

public sealed record McpConnectionOptions
{
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan CatalogTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public McpClientOptions? Client { get; init; }
    public bool RefreshOnNotification { get; init; } = true;

    internal void Validate()
    {
        if (StartupTimeout <= TimeSpan.Zero || StartupTimeout > TimeSpan.FromMinutes(10) ||
            CatalogTimeout <= TimeSpan.Zero || CatalogTimeout > TimeSpan.FromMinutes(10) ||
            CallTimeout <= TimeSpan.Zero || CallTimeout > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(StartupTimeout), "MCP deadlines must be positive and within their configured bounds.");
    }
}