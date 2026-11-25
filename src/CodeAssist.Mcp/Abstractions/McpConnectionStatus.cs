using CodeAssist.Tools;
namespace CodeAssist.Mcp;

public sealed record McpConnectionStatus(string Server, bool Connected, int Tools);