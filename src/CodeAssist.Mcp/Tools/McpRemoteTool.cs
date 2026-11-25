using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Mcp;

public sealed class McpRemoteTool(IMcpConnections connections, Func<ToolContext, ToolSpec, bool>? visible = null) : ITool
{
    public ToolSpec Spec { get; } = new("mcp.remote", "List or call tools on an authenticated session-owned MCP connection. Remote inputs are validated against the connected tool schema. Calls always require network and execution authorization.",
        """{"type":"object","properties":{"operation":{"enum":["list","call"]},"server":{"type":"string"},"tool":{"type":"string","minLength":1,"maxLength":128},"input":{"type":"object"}},"required":["operation","server"],"additionalProperties":false}""",
        ToolRisk.Execute, ["mcp", "exec", "net"]);

    public ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Parse(arguments);
        var server = arguments.RequireString("server");
        return new(new ToolPlan(arguments.RequireString("operation") == "list" ? [new(ToolEffectKind.Read, $"mcp:{server}:tools"), new(ToolEffectKind.Net, $"mcp:{server}")] :
            [new(ToolEffectKind.Net, $"mcp:{server}"), new(ToolEffectKind.Exec, $"mcp:{server}:{arguments.RequireString("tool")}")]));
    }

    public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        Parse(arguments);
        return ToolSupport.FromPortAsync(async () =>
        {
            if (visible is null) return ToolResult.Error("Remote execution requires a host visibility policy.");
            var server = arguments.RequireString("server");
            var tools = (await connections.ListAsync(server, context, cancellationToken)).Where(spec => visible(context, spec)).ToArray();
            if (arguments.RequireString("operation") == "list") return ToolSupport.BoundedText(JsonSerializer.Serialize(tools), context);
            var name = arguments.RequireString("tool");
            if (!tools.Any(spec => spec.Name == $"mcp.{server}.{name}")) return ToolResult.Error("Remote tool is unavailable for the current role.");
            return await connections.CallAsync(server, name, ToolArguments.Parse(arguments.Json.GetProperty("input").GetRawText()), context, cancellationToken);
        }, cancellationToken);
    }

    private void Parse(ToolArguments arguments)
    {
        var operation = arguments.RequireString("operation");
        if (operation is not ("list" or "call") || !connections.Servers.Contains(arguments.RequireString("server"), StringComparer.Ordinal)) arguments.Issue("Unknown operation or configured server.");
        if (operation == "call")
        {
            var tool = arguments.RequireString("tool");
            if (string.IsNullOrWhiteSpace(tool) || tool.Length > 128 || tool.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.')) arguments.Issue("Invalid remote tool name.");
            if (!arguments.Has("input") || arguments.Json.GetProperty("input").ValueKind != JsonValueKind.Object) arguments.Issue("Remote call requires an input object.");
        }
        arguments.Validate();
    }
}