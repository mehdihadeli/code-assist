using CodeAssist.Tools;
namespace CodeAssist.Mcp;

public interface IMcpConnections
{
    IReadOnlyList<string> Servers { get; }
    IReadOnlyList<ToolEffect> ConnectEffects(string server) => [new(ToolEffectKind.Net, $"mcp:{server}")];
    Task<McpConnectionStatus> StatusAsync(string server, ToolContext context, CancellationToken cancellationToken);
    Task<McpConnectionStatus> ConnectAsync(string server, ToolContext context, CancellationToken cancellationToken);
    Task DisconnectAsync(string server, ToolContext context, CancellationToken cancellationToken);
    Task ForgetCredentialsAsync(string server, ToolContext context, CancellationToken cancellationToken) => throw new NotSupportedException("This host does not support credential removal.");
    Task ReleaseSessionAsync(ToolContext context, CancellationToken cancellationToken) => Task.WhenAll(Servers.Select(server => DisconnectAsync(server, context, cancellationToken)));
    Task<IReadOnlyList<ToolSpec>> ListAsync(string server, ToolContext context, CancellationToken cancellationToken);
    Task<ToolResult> CallAsync(string server, string tool, ToolArguments arguments, ToolContext context, CancellationToken cancellationToken);
}