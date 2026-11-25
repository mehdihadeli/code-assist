using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAssist.Tools;
using CodeAssist.Models.Chat;
using CodeAssist.Configuration;

namespace CodeAssist.Server.Composition;

public static class HostedConnectorComposition
{
    public static ProviderConnection Apply(ProviderConnection connection, HostedConnectorsSettings settings, IReadOnlyDictionary<string, string> environment, Func<string, bool>? admit)
    {
        if (!settings.Enabled || settings.Servers.Count == 0) return connection;
        if (!settings.AcceptRemoteExecution || admit is null) throw new UnauthorizedAccessException("Hosted connectors require explicit remote-execution acknowledgment and host admission.");
        if (settings.Servers.Count > 16) throw new ArgumentException("At most 16 hosted connectors may be configured.");
        var profiles = settings.Servers.Select(pair =>
        {
            var id = pair.Key;
            var definition = pair.Value;
            if (!admit(id)) throw new UnauthorizedAccessException("Hosted connector use is restricted by host policy.");
            if (id.Length is < 1 or > 80 || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')) throw new ArgumentException("Invalid hosted connector identity.");
            if (!Uri.TryCreate(definition.Url, UriKind.Absolute, out var address) || address.Scheme != "https" || address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0)
                throw new ArgumentException("Hosted connectors require HTTPS endpoints without embedded credentials.");
            if (definition.ToolRules.Count > 256 || definition.FixedTools.Count > 256 || definition.CacheSeconds is not (0 or 300 or 3600)) throw new ArgumentException("Invalid hosted toolset limits or cache lifetime.");
            string? token = null;
            if (definition.Token is { } reference)
            {
                if (!EnvironmentReference.TryParse(reference, out var variable) || !environment.TryGetValue(variable, out token) || string.IsNullOrWhiteSpace(token) || token.Contains("{env:", StringComparison.Ordinal))
                    throw new ArgumentException("Hosted bearer tokens require an available, complete environment reference.");
            }
            var rules = new JsonObject();
            foreach (var rule in definition.ToolRules)
            {
                if (rule.Key.Length is < 1 or > 128) throw new ArgumentException("Invalid hosted tool name.");
                var value = new JsonObject();
                if (rule.Value.Enabled is { } enabled) value["enabled"] = enabled;
                if (rule.Value.Deferred is { } deferred) value["defer_loading"] = deferred;
                rules.Add(rule.Key, value);
            }
            var toolset = new JsonObject
            {
                ["type"] = "mcp_toolset", ["mcp_server_name"] = id,
                ["default_config"] = new JsonObject { ["enabled"] = definition.DefaultEnabled, ["defer_loading"] = definition.Deferred }, ["configs"] = rules,
            };
            if (definition.CacheSeconds > 0) toolset["cache_control"] = new JsonObject { ["type"] = "ephemeral", ["ttl"] = definition.CacheSeconds == 300 ? "5m" : "1h" };
            if (definition.FixedTools.Count > 0)
            {
                if (definition.FixedTools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != definition.FixedTools.Count) throw new ArgumentException("Fixed hosted tool names must be unique.");
                toolset["tools"] = new JsonArray(definition.FixedTools.Select(tool =>
                {
                    if (tool.Name.Length is < 1 or > 128 || tool.Description.Length > 8000) throw new ArgumentException("Invalid fixed hosted tool metadata.");
                    _ = new ToolInputSchema(tool.Schema);
                    return (JsonNode)new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description, ["input_schema"] = JsonNode.Parse(tool.Schema) };
                }).ToArray());
            }
            if (toolset.ToJsonString().Length > 262_144) throw new ArgumentException("Hosted toolset exceeds its metadata budget.");
            return new HostedConnectorProfile { Id = id, Endpoint = address, BearerToken = token, Toolset = JsonSerializer.SerializeToElement(toolset) };
        }).ToArray();
        var headers = new Dictionary<string, string>(connection.Headers, StringComparer.OrdinalIgnoreCase);
        var beta = settings.Servers.Values.Any(server => server.FixedTools.Count > 0) ? "mcp-client-2026-09-15" : "mcp-client-2025-11-20";
        headers["anthropic-beta"] = headers.TryGetValue("anthropic-beta", out var existing) ? existing + "," + beta : beta;
        return connection with { HostedExecutionEnabled = true, HostedConnectors = profiles, Headers = headers };
    }
}