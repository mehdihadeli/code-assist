using CodeAssist.Tools;
using ModelContextProtocol.Authentication;

namespace CodeAssist.Mcp;

public sealed record AuthenticatedMcpServer(string Id, Uri Endpoint, Func<ToolContext, ClientOAuthOptions> OAuth);