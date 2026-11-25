using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAssist.Configuration;

/// <summary>Key paths an administrator locked in the managed file. Patterns use <c>*</c> and match a path or any child of it.</summary>
public sealed class LockSet
{
    private readonly List<Regex> _patterns = [];

    public IReadOnlyList<string> Patterns { get; }

    public LockSet(IEnumerable<string> patterns)
    {
        Patterns = patterns.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToArray();
        foreach (var pattern in Patterns)
        {
            var body = Regex.Escape(pattern).Replace("\\*", ".*");
            _patterns.Add(new Regex($"^{body}(?:[.\\[].*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)));
        }
    }

    public static LockSet Empty { get; } = new([]);

    public bool IsLocked(string path) => _patterns.Any(p => p.IsMatch(path));

    /// <summary>True when the path is locked or sits above a locked key (writing it would replace the locked value).</summary>
    public bool Touches(string path) =>
        IsLocked(path) || Patterns.Any(p => p.StartsWith(path + ".", StringComparison.OrdinalIgnoreCase) || p.StartsWith(path + "[", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Applies one canonical tier onto the effective tree, enforcing trust, strictness and locks and recording provenance.</summary>
internal sealed class Merger(
    KeyCatalog catalog,
    JsonObject effective,
    LockSet locks,
    DiagnosticBag diags,
    Dictionary<string, SettingOrigin> origins)
{
    public void Merge(JsonObject tierTree, SettingsTier tier, string source) =>
        MergeObject(effective, tierTree, catalog.Root, [], tier, source);

    private void MergeObject(JsonObject target, JsonObject incoming, KeyNode node, List<string> path, SettingsTier tier, string source)
    {
        foreach (var (name, value) in incoming.ToArray())
        {
            var child = node.Kind == KeyKind.Object ? node.Children[name] : node.Element!;
            path.Add(name);
            try
            {
                var text = SettingsPath.Format(path);

                if (TrustRejection(child, tier) is { } trust)
                {
                    Reject(text, tier, source, child.IsSecret ? "(secret)" : Preview(value), trust, DiagnosticSeverity.Warning);
                    continue;
                }

                if (tier != SettingsTier.Managed && tier != SettingsTier.Defaults && locks.IsLocked(text))
                {
                    Reject(text, tier, source, Preview(SettingsSnapshot.RedactValue(value, child)), "locked by managed settings", DiagnosticSeverity.Warning);
                    continue;
                }

                if (value is null)
                {
                    if (tier == SettingsTier.Project && child.Strict != StrictDirection.None)
                    {
                        Reject(text, tier, source, "(unset)", "a project file may not remove a limit", DiagnosticSeverity.Warning);
                        continue;
                    }
                    target.Remove(name);
                    Accept(text, tier, source, "(unset)");
                    continue;
                }

                switch (child.Kind)
                {
                    case KeyKind.Object or KeyKind.Dictionary:
                        if (target[name] is not JsonObject nested)
                        {
                            nested = new JsonObject();
                            target[name] = nested;
                        }
                        MergeObject(nested, (JsonObject)value, child, path, tier, source);
                        break;
                    case KeyKind.List:
                        MergeList(target, name, (JsonArray)value, child, text, tier, source);
                        break;
                    default:
                        MergeScalar(target, name, value, child, text, tier, source);
                        break;
                }
            }
            finally
            {
                path.RemoveAt(path.Count - 1);
            }
        }
    }

    private void MergeScalar(JsonObject target, string name, JsonNode value, KeyNode node, string text, SettingsTier tier, string source)
    {
        var current = target[name];
        var shown = node.IsSecret ? "(secret)" : Preview(value);
        if (tier == SettingsTier.Project && current is not null && StrictViolation(node, current, value) is { } violation)
        {
            Reject(text, tier, source, shown, violation, DiagnosticSeverity.Warning);
            return;
        }
        target[name] = value.DeepClone();
        Accept(text, tier, source, shown);
    }

    private void MergeList(JsonObject target, string name, JsonArray incoming, KeyNode node, string text, SettingsTier tier, string source)
    {
        var current = target[name] as JsonArray;

        switch (node.Merge)
        {
            case MergeMode.Rules:
                var rules = current ?? new JsonArray();
                target[name] = rules;
                foreach (var item in incoming)
                {
                    if (item is not JsonObject rule) continue;
                    var decision = rule.TryGetPropertyValue("decision", out var d) ? d?.GetValue<string>() : null;
                    if (tier == SettingsTier.Project && !string.Equals(decision, "deny", StringComparison.OrdinalIgnoreCase))
                    {
                        Reject(text, tier, source, Preview(rule), "a project file may only add deny rules", DiagnosticSeverity.Warning);
                        continue;
                    }
                    var copy = (JsonObject)rule.DeepClone();
                    copy["origin"] = TierName(tier);
                    rules.Add(copy);
                    Accept(text, tier, source, Preview(copy));
                }
                return;

            case MergeMode.Append:
                var merged = current ?? new JsonArray();
                target[name] = merged;
                foreach (var item in incoming)
                {
                    if (item is null || merged.Any(existing => Same(existing, item))) continue;
                    merged.Add(item.DeepClone());
                }
                Accept(text, tier, source, Preview(incoming));
                return;

            default:
                if (tier == SettingsTier.Project && node.Strict == StrictDirection.Subset && current is { Count: > 0 })
                {
                    var outside = incoming.FirstOrDefault(item => !current.Any(existing => Same(existing, item)));
                    if (outside is not null)
                    {
                        Reject(text, tier, source, Preview(incoming), $"a project file may only narrow this list ('{Preview(outside)}' is not in it)", DiagnosticSeverity.Warning);
                        return;
                    }
                }
                target[name] = incoming.DeepClone();
                Accept(text, tier, source, Preview(incoming));
                return;
        }
    }

    private static string? TrustRejection(KeyNode node, SettingsTier tier)
    {
        if (tier == SettingsTier.Defaults) return null;
        if (tier == SettingsTier.Preferences && node.Trust != TrustClass.UserOnly)
            return "preferences.json only holds personal display preferences";
        return node.Trust switch
        {
            TrustClass.Sensitive or TrustClass.UserOnly when tier == SettingsTier.Project => "not accepted from a project file",
            TrustClass.ManagedOnly when tier != SettingsTier.Managed => "only the managed tier may set this",
            _ => null,
        };
    }

    private static string? StrictViolation(KeyNode node, JsonNode current, JsonNode value)
    {
        switch (node.Strict)
        {
            case StrictDirection.Min or StrictDirection.Max when node.IsNumeric:
                if (!SettingsJson.TryNumber(current, out var cur) || !SettingsJson.TryNumber(value, out var next)) return null;
                if (node.Strict == StrictDirection.Min && next > cur) return $"a project file may only lower this (current {cur})";
                if (node.Strict == StrictDirection.Max && next < cur) return $"a project file may only raise this (current {cur})";
                return null;
            case StrictDirection.Order when node.StrictOrder.Count > 0:
                var currentIndex = IndexOf(node.StrictOrder, current.GetValue<string>());
                var nextIndex = IndexOf(node.StrictOrder, value.GetValue<string>());
                return nextIndex > currentIndex ? $"a project file may only move toward '{node.StrictOrder[0]}' (current {current.GetValue<string>()})" : null;
            default:
                return null;
        }

        static int IndexOf(IReadOnlyList<string> order, string item)
        {
            for (var i = 0; i < order.Count; i++)
                if (string.Equals(order[i], item, StringComparison.OrdinalIgnoreCase)) return i;
            return int.MaxValue;
        }
    }

    private static bool Same(JsonNode? left, JsonNode? right) =>
        left is null || right is null ? left is null && right is null
        : string.Equals(left.ToJsonString(), right.ToJsonString(), StringComparison.OrdinalIgnoreCase);

    private SettingOrigin Origin(string path)
    {
        if (!origins.TryGetValue(path, out var origin))
            origins[path] = origin = new SettingOrigin(path);
        return origin;
    }

    private void Accept(string path, SettingsTier tier, string source, string value) =>
        Origin(path).Candidates.Add(new SettingCandidate(tier, source, value, true, null));

    private void Reject(string path, SettingsTier tier, string source, string value, string reason, DiagnosticSeverity severity)
    {
        Origin(path).Candidates.Add(new SettingCandidate(tier, source, value, false, reason));
        diags.Add(severity, "value_rejected", $"{path}: {reason}.", path, tier, source);
    }

    public static string TierName(SettingsTier tier) => SettingsJson.CamelCase(tier.ToString());

    private static string Preview(JsonNode? node)
    {
        var text = node?.ToJsonString() ?? "(unset)";
        return text.Length > 200 ? text[..200] + "…" : text;
    }
}
