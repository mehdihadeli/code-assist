using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CodeAssist.Models.Chat;

public sealed class HostedConnectorProfile
{
    public required string Id { get; init; }
    public required Uri Endpoint { get; init; }
    [JsonIgnore] public string? BearerToken { get; init; }
    public required JsonElement Toolset { get; init; }
    private bool UsesDeferred => Toolset.GetProperty("default_config").GetProperty("defer_loading").GetBoolean() ||
        Toolset.GetProperty("configs").EnumerateObject().Any(rule => rule.Value.TryGetProperty("defer_loading", out var deferred) && deferred.GetBoolean());

    internal JsonObject Server() => new() { ["type"] = "url", ["name"] = Id, ["url"] = Endpoint.AbsoluteUri, ["authorization_token"] = BearerToken };

    internal static void Apply(JsonObject body, IReadOnlyList<HostedConnectorProfile> profiles)
    {
        if (body["mcp_servers"] is JsonArray { Count: > 0 }) throw new UnauthorizedAccessException("Use trusted hosted connector profiles, not request-level server definitions.");
        body["mcp_servers"] = new JsonArray(profiles.Select(profile => (JsonNode)profile.Server()).ToArray());
        if (body["tools"] is not null and not JsonArray) throw new ArgumentException("Hosted connector tools require a JSON array.");
        var tools = body["tools"] as JsonArray ?? new JsonArray();
        if (body["tools"] is null) body["tools"] = tools;
        foreach (var profile in profiles) tools.Add(JsonNode.Parse(profile.Toolset.GetRawText()));
        if (profiles.Any(profile => profile.UsesDeferred) &&
            !tools.Any(tool => tool?["type"]?.GetValue<string>() == "tool_search_tool_regex_20251119"))
            tools.Add(new JsonObject { ["type"] = "tool_search_tool_regex_20251119", ["name"] = "tool_search_tool_regex" });
    }
}