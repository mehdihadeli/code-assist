using System.Text.Json.Nodes;

namespace CodeAssist.Configuration;

/// <summary>Values set for one invocation (command-line flags). Never persisted.</summary>
public sealed class FlagOverrides
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> Values => _values;
    public bool Any => _values.Count > 0;

    public void Set(string path, string rawValue) => _values[path] = rawValue;

    public void Clear() => _values.Clear();
}

/// <summary>Turns flat path/value pairs (environment variables, flags) into a raw JSON tree that the canonicalizer then types.</summary>
internal static class OverrideTree
{
    private const string Separator = "__";

    public static JsonObject FromEnvironment(KeyCatalog catalog, IReadOnlyDictionary<string, string> environment, DiagnosticBag diags)
    {
        var root = new JsonObject();
        foreach (var (name, raw) in environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var segments = name.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0 || catalog.Root.Step(segments[0]) is null) continue;
            Set(root, catalog, segments, raw, diags, SettingsTier.Environment, $"env:{name}");
        }
        return root;
    }

    public static JsonObject FromFlags(KeyCatalog catalog, IReadOnlyDictionary<string, string> flags, DiagnosticBag diags)
    {
        var root = new JsonObject();
        foreach (var (path, raw) in flags)
        {
            IReadOnlyList<string> segments;
            try { segments = SettingsPath.Parse(path); }
            catch (FormatException ex)
            {
                diags.Error("invalid_path", $"{path}: {ex.Message}", path, SettingsTier.Flags, $"flag:{path}");
                continue;
            }
            Set(root, catalog, segments, raw, diags, SettingsTier.Flags, $"flag:{path}");
        }
        return root;
    }

    private static void Set(JsonObject root, KeyCatalog catalog, IReadOnlyList<string> segments, string raw, DiagnosticBag diags, SettingsTier tier, string source)
    {
        JsonNode container = root;
        var node = catalog.Root;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var next = node.Step(segment);
            if (next is null)
            {
                diags.Warn("unknown_key", $"'{SettingsPath.Format(segments)}' is not a setting.", SettingsPath.Format(segments), tier, source);
                return;
            }

            var last = i == segments.Count - 1;
            JsonNode? fresh = last ? ValueFor(next, raw) : next.Kind == KeyKind.List ? new JsonArray() : new JsonObject();

            if (container is JsonObject obj)
            {
                var key = obj.Select(pair => pair.Key).FirstOrDefault(k => string.Equals(k, segment, StringComparison.OrdinalIgnoreCase)) ?? segment;
                if (!last && obj[key] is { } existing) container = existing;
                else
                {
                    obj[key] = fresh;
                    container = fresh!;
                    if (last) return;
                }
            }
            else if (container is JsonArray array)
            {
                var index = int.Parse(segment);
                while (array.Count <= index) array.Add(null);
                if (!last && array[index] is { } existing) container = existing;
                else
                {
                    array[index] = fresh;
                    container = fresh!;
                    if (last) return;
                }
            }
            node = next;
        }
    }

    internal static JsonNode? ValueFor(KeyNode node, string raw)
    {
        var text = raw.Trim();
        switch (node.Kind)
        {
            case KeyKind.List:
                if (text.StartsWith('[')) return TryParse(text) ?? new JsonArray(raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
                return new JsonArray(raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            case KeyKind.Object or KeyKind.Dictionary:
                return text.StartsWith('{') ? TryParse(text) ?? JsonValue.Create(raw) : JsonValue.Create(raw);
            default:
                return JsonValue.Create(raw);
        }
    }

    private static JsonNode? TryParse(string text)
    {
        try { return JsonNode.Parse(text, documentOptions: SettingsJson.DocumentOptions); }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
