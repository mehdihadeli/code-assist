using System.Text.Json.Nodes;

namespace CodeAssist.Configuration;

public sealed record LoadedFile(SettingsTier Tier, string Path, bool Exists);

public sealed record SettingLeaf(string Path, JsonNode? Value, SettingOrigin Origin, KeyNode Node);

/// <summary>An immutable, fully resolved view of the settings for one moment, with provenance and diagnostics.</summary>
public sealed class SettingsSnapshot
{
    public required string Id { get; init; }
    public required CodeAssistSettings Settings { get; init; }
    public required JsonObject Effective { get; init; }
    public required IReadOnlyDictionary<string, SettingOrigin> Origins { get; init; }
    public required IReadOnlyList<SettingsDiagnostic> Diagnostics { get; init; }
    public required IReadOnlyList<LoadedFile> Files { get; init; }
    public required LockSet Locks { get; init; }
    public required SettingsLocations Locations { get; init; }

    public bool IsValid => Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error);

    public SettingOrigin OriginOf(string path) =>
        Origins.TryGetValue(path, out var origin) ? origin : new SettingOrigin(path);

    /// <summary>Finds the effective value at a path (case-insensitive property names), or null when absent.</summary>
    public JsonNode? Find(IReadOnlyList<string> segments) => FindIn(Effective, segments);

    /// <summary>Walks a settings-shaped tree by path segments (case-insensitive names, numeric list indices).</summary>
    public static JsonNode? FindIn(JsonNode? root, IReadOnlyList<string> segments)
    {
        JsonNode? current = root;
        foreach (var segment in segments)
        {
            current = current switch
            {
                JsonObject obj => obj.FirstOrDefault(p => string.Equals(p.Key, segment, StringComparison.OrdinalIgnoreCase)).Value,
                JsonArray array when int.TryParse(segment, out var i) && i >= 0 && i < array.Count => array[i],
                _ => null,
            };
            if (current is null) return null;
        }
        return current;
    }

    /// <summary>Effective tree with secret values replaced by a marker.</summary>
    public JsonObject Redacted()
    {
        var copy = (JsonObject)Effective.DeepClone();
        Redact(copy, KeyCatalog.Default.Root);
        return copy;
    }

    public static JsonNode? RedactValue(JsonNode? value, KeyNode node)
    {
        if (value is null) return null;
        if (node.IsSecret) return JsonValue.Create("(secret)");
        var copy = value.DeepClone();
        if (copy is JsonObject obj) Redact(obj, node);
        return copy;
    }

    private static void Redact(JsonObject obj, KeyNode node)
    {
        foreach (var (name, value) in obj.ToArray())
        {
            var child = node.Kind == KeyKind.Object ? node.Children.GetValueOrDefault(name) : node.Element;
            if (child is null || value is null) continue;
            if (child.IsSecret) obj[name] = JsonValue.Create("(secret)");
            else if (value is JsonObject nested) Redact(nested, child);
            else if (value is JsonArray array && child.Element is { Kind: KeyKind.Object } element)
                foreach (var item in array.OfType<JsonObject>()) Redact(item, element);
        }
    }

    /// <summary>Every leaf in the effective tree (scalars, lists and empty maps) with its origin, secrets masked.</summary>
    public IEnumerable<SettingLeaf> Leaves()
    {
        var path = new List<string>();
        foreach (var leaf in Walk(Effective, KeyCatalog.Default.Root, path)) yield return leaf;
    }

    private IEnumerable<SettingLeaf> Walk(JsonNode? value, KeyNode node, List<string> path)
    {
        if (value is JsonObject obj && node.Kind is KeyKind.Object or KeyKind.Dictionary)
        {
            if (obj.Count == 0 && path.Count > 0)
            {
                var emptyText = SettingsPath.Format(path);
                yield return new SettingLeaf(emptyText, obj.DeepClone(), OriginOf(emptyText), node);
                yield break;
            }
            foreach (var (name, child) in obj)
            {
                var childNode = node.Kind == KeyKind.Object ? node.Children.GetValueOrDefault(name) : node.Element;
                if (childNode is null) continue;
                path.Add(name);
                foreach (var leaf in Walk(child, childNode, path)) yield return leaf;
                path.RemoveAt(path.Count - 1);
            }
            yield break;
        }

        var text = SettingsPath.Format(path);
        yield return new SettingLeaf(text, RedactValue(value, node), OriginOf(text), node);
    }
}
