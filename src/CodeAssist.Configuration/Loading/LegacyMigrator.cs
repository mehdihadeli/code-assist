using System.Text.Json.Nodes;

namespace CodeAssist.Configuration;

/// <summary>Rewrites keys from the previous release's settings.json shape to the current model. File tiers only.</summary>
internal static class LegacyMigrator
{
    public static void Migrate(JsonObject root, DiagnosticBag diags, SettingsTier tier, string source)
    {
        if (Take(root, "provider") is JsonObject provider)
        {
            var target = GetOrCreate(GetOrCreate(root, "providers"), "default");
            foreach (var (key, value) in provider.ToArray())
            {
                provider.Remove(key);
                target[key] = value;
            }
            Migrated("Provider", "providers.default");
        }

        if (Take(root, "mcp") is JsonObject mcp)
        {
            var existing = Find(root, "connectors") as JsonObject;
            if (existing is null) root["connectors"] = mcp;
            else
                foreach (var (key, value) in mcp.ToArray())
                {
                    mcp.Remove(key);
                    existing[key] = value;
                }
            Migrated("Mcp", "connectors");
        }

        if (Find(root, "agent") is JsonObject agent)
        {
            if (Take(agent, "permissionMode") is { } mode)
            {
                agent["permissionPreset"] = mode;
                Migrated("Agent.PermissionMode", "agent.permissionPreset");
            }
            if (Take(agent, "maxContextCharacters") is { } maxContext)
            {
                GetOrCreate(root, "context")["maxCharacters"] = maxContext;
                Migrated("Agent.MaxContextCharacters", "context.maxCharacters");
            }
        }

        if (Take(root, "console") is not null)
            diags.Info("legacy_key_ignored", "'Console' settings moved to the 'cli' section and were not migrated automatically.", "console", tier, source);

        void Migrated(string from, string to) =>
            diags.Info("legacy_key_migrated", $"Legacy key '{from}' was read as '{to}'. Update the file to the new name.", to, tier, source);
    }

    private static string? FindKey(JsonObject obj, string name) =>
        obj.Select(pair => pair.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

    private static JsonNode? Find(JsonObject obj, string name) => FindKey(obj, name) is { } key ? obj[key] : null;

    private static JsonNode? Take(JsonObject obj, string name)
    {
        if (FindKey(obj, name) is not { } key) return null;
        var value = obj[key];
        obj.Remove(key);
        return value;
    }

    private static JsonObject GetOrCreate(JsonObject obj, string name)
    {
        if (Find(obj, name) is JsonObject existing) return existing;
        var created = new JsonObject();
        obj[name] = created;
        return created;
    }
}
