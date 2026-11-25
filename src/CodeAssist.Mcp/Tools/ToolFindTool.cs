using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Mcp;

public sealed class ToolFindTool(Func<ToolContext, IReadOnlyList<ToolSpec>> visibleTools, IMcpConnections? connections = null, Func<ToolContext, ToolSpec, bool>? discoveredVisible = null, IToolExposure? exposure = null) : ITool
{
    public ToolSpec Spec { get; } = new(
        "tool.find", "Search tools by name, description and tag. Results respect role and disabled-tool settings. Set activate=true to advertise this local page's schemas on subsequent model turns when supported. Activation never executes or authorizes a tool. Remote tools remain invoked through mcp.remote.",
        """
        {"type":"object","properties":{"query":{"type":"string"},"server":{"type":"string","minLength":1,"maxLength":80},"offset":{"type":"integer","minimum":0},
         "limit":{"type":"integer","minimum":1,"maximum":50},"includeSchemas":{"type":"boolean"},"activate":{"type":"boolean"}},"additionalProperties":false}
        """, ToolRisk.Read, ["read"]);

    public ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = Parse(arguments);
        return new(new ToolPlan(request.Server is null ? [new(ToolEffectKind.Read, "catalog:tools")] :
            [new(ToolEffectKind.Read, "catalog:tools"), new(ToolEffectKind.Net, $"mcp:{request.Server}")]));
    }

    public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var request = Parse(arguments);
        return ToolSupport.FromPortAsync(async () =>
        {
            var available = visibleTools(context);
            if (request.Server is not null)
            {
                if (connections is null || discoveredVisible is null || !available.Any(tool => tool.Name == "mcp.remote"))
                    return ToolResult.Error("Remote discovery is unavailable for the current role.");
                if (!connections.Servers.Contains(request.Server, StringComparer.Ordinal)) return ToolResult.Error("Unknown configured server.");
                available = (await connections.ListAsync(request.Server, context, cancellationToken)).Where(tool => discoveredVisible(context, tool)).ToArray();
            }
            var matches = available.Where(tool => request.Terms.All(term =>
                    $"{tool.Name} {tool.Description} {string.Join(' ', tool.Tags)}".Contains(term, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray();
            var selected = matches.Skip(request.Offset).Take(request.Limit).ToArray();
            if (request.Activate && (exposure is null || request.Server is not null)) return ToolResult.Error("Schema activation is unavailable here; remote tools use mcp.remote.");
            var activated = request.Activate ? exposure!.Activate(selected, context) : [];
            var page = selected.Select(tool => new
            {
                tool.Name, tool.Description, tool.ReadOnly, tool.Tags,
                schema = request.Schemas ? JsonSerializer.Deserialize<JsonElement>(tool.InputSchema) : (JsonElement?)null,
                invocation = request.Server is null ? null : new { tool = "mcp.remote", server = request.Server, name = tool.Name[("mcp." + request.Server + ".").Length..] },
            });
            var text = JsonSerializer.Serialize(new
            {
                total = matches.Length,
                activated,
                nextOffset = (long)request.Offset + request.Limit < matches.Length ? request.Offset + request.Limit : (int?)null,
                tools = page,
            });
            return ToolSupport.BoundedText(text, context);
        }, cancellationToken);
    }

    private static ToolFindRequest Parse(ToolArguments arguments)
    {
        var query = arguments.String("query") ?? "";
        var offset = arguments.Int("offset", 0) ?? 0;
        var limit = arguments.Int("limit", 1, 50) ?? 20;
        var schemas = arguments.Bool("includeSchemas");
        var activate = arguments.Bool("activate");
        var server = arguments.String("server");
        if (server is not null && (string.IsNullOrWhiteSpace(server) || server.Length > 80)) arguments.Issue("Invalid discovery server.");
        if (query.Length > 2000) arguments.Issue("query exceeds 2000 characters.");
        arguments.Validate();
        return new(query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), offset, limit, schemas, server, activate);
    }
}