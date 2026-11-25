using CodeAssist.Tools;
namespace CodeAssist.Mcp;

public sealed record McpPromptArgument(string Name, string? Description, bool Required);
public sealed record McpPromptEntry(string Name, string? Description, IReadOnlyList<McpPromptArgument> Arguments);
public sealed record McpPromptMessage(string Role, ToolResult Content);
public sealed record McpPromptResult(string? Description, IReadOnlyList<McpPromptMessage> Messages);

public interface IMcpPrompts
{
    Task<IReadOnlyList<McpPromptEntry>> ListPromptsAsync(string server, ToolContext context, CancellationToken cancellationToken);
    Task<McpPromptResult> GetPromptAsync(string server, string name, IReadOnlyDictionary<string, string> arguments,
        ToolContext context, CancellationToken cancellationToken);
}