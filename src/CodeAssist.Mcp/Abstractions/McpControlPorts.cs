using CodeAssist.Tools;
namespace CodeAssist.Mcp;

public sealed record McpControlPorts(IMcpConnections Connections, Func<Guid, ToolContext> Context, IMcpPrompts? Prompts = null);