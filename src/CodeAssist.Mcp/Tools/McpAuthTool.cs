using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Mcp;

public sealed class McpAuthTool(IMcpConnections connections, IUserInteraction interaction) : ITool
{
    public ToolSpec Spec { get; } = new("mcp.auth", "Inspect, authenticate or disconnect a host-configured MCP server for this session. Authentication requires trusted consent and SDK state-aware OAuth; no credentials are accepted or returned.",
        """{"type":"object","properties":{"operation":{"enum":["status","connect","disconnect"]},"server":{"type":"string","minLength":1,"maxLength":80}},"required":["operation","server"],"additionalProperties":false}""",
        ToolRisk.Network, ["mcp", "net", "authentication"]);

    public ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (operation, server) = Parse(arguments);
        return new(new ToolPlan(operation == "connect" ? [.. connections.ConnectEffects(server), new(ToolEffectKind.Ask, $"session:{context.SessionId}:mcp-auth:{server}")] :
            [new(operation == "status" ? ToolEffectKind.Read : ToolEffectKind.Write, $"session:{context.SessionId}:mcp:{server}")]));
    }

    public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (operation, server) = Parse(arguments);
        return ToolSupport.FromPortAsync(async () =>
        {
            if (operation == "connect")
            {
                var answers = await interaction.AskAsync([new("Authenticate MCP", $"Authenticate configured server '{server}' for this session?", [new("Authenticate"), new("Cancel")], false)], context, cancellationToken);
                if (answers is not { Count: 1 } || answers[0].Choices.Count != 1 || answers[0].Choices[0] != "Authenticate") return ToolResult.Error("Authentication cancelled.");
                return ToolResult.Ok(JsonSerializer.Serialize(await connections.ConnectAsync(server, context, cancellationToken)));
            }
            if (operation == "disconnect") await connections.DisconnectAsync(server, context, cancellationToken);
            return ToolResult.Ok(JsonSerializer.Serialize(await connections.StatusAsync(server, context, cancellationToken)));
        }, cancellationToken);
    }

    private (string Operation, string Server) Parse(ToolArguments arguments)
    {
        var operation = arguments.RequireString("operation");
        var server = arguments.RequireString("server");
        if (operation is not ("status" or "connect" or "disconnect") || !connections.Servers.Contains(server, StringComparer.Ordinal)) arguments.Issue("Unknown operation or configured MCP server.");
        arguments.Validate();
        return (operation, server);
    }
}