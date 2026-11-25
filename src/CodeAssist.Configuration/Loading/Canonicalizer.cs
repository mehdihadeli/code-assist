using System.Text.Json.Nodes;

namespace CodeAssist.Configuration;

/// <summary>
/// Normalizes one tier's raw JSON: canonical key spelling, typed scalars, wrapped single-item lists, range and enum checks.
/// Anything invalid is dropped with a diagnostic so a bad value never reaches the merge.
/// </summary>
internal sealed class Canonicalizer(KeyCatalog catalog, DiagnosticBag diags)
{
    private static bool IsFileTier(SettingsTier tier) => tier is SettingsTier.Preferences or SettingsTier.User or SettingsTier.Project or SettingsTier.Managed;

    public JsonObject Run(JsonObject input, SettingsTier tier, string source, JsonObject? existing)
    {
        var result = new JsonObject();
        Fill(result, input, catalog.Root, [], tier, source, existing);
        return result;
    }

    /// <summary>Canonicalizes a single value for a known node (used by the editor).</summary>
    public (bool Ok, JsonNode? Node) RunNode(JsonNode? value, KeyNode node, IReadOnlyList<string> path, SettingsTier tier, string source) =>
        Convert(value, node, path.ToList(), tier, source, null);

    private void Fill(JsonObject target, JsonObject input, KeyNode node, List<string> path, SettingsTier tier, string source, JsonObject? existing)
    {
        foreach (var (name, value) in input)
        {
            if (name.StartsWith('$')) continue;

            KeyNode? child;
            string key;
            if (node.Kind == KeyKind.Object)
            {
                if (!node.Children.TryGetValue(name, out child))
                {
                    path.Add(name);
                    diags.Warn("unknown_key", $"'{SettingsPath.Format(path)}' is not a setting and was ignored.", SettingsPath.Format(path), tier, source);
                    path.RemoveAt(path.Count - 1);
                    continue;
                }
                key = child.Name;
            }
            else
            {
                child = node.Element!;
                key = existing?.Select(pair => pair.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? name;
            }

            path.Add(key);
            var existingChild = existing is not null && existing.TryGetPropertyValue(key, out var e) ? e : null;
            var (ok, converted) = Convert(value, child, path, tier, source, existingChild);
            if (ok) target[key] = converted;
            path.RemoveAt(path.Count - 1);
        }
    }

    private (bool Ok, JsonNode? Node) Convert(JsonNode? value, KeyNode node, List<string> path, SettingsTier tier, string source, JsonNode? existing)
    {
        if (value is null) return (true, null);

        switch (node.Kind)
        {
            case KeyKind.Object or KeyKind.Dictionary:
                if (value is not JsonObject obj)
                    return Fail("type_mismatch", "expected an object", path, tier, source);
                var target = new JsonObject();
                Fill(target, obj, node, path, tier, source, existing as JsonObject);
                return (true, target);

            case KeyKind.List:
                var items = value is JsonArray array ? array.ToArray() : [value];
                var list = new JsonArray();
                for (var i = 0; i < items.Length; i++)
                {
                    if (items[i] is null) continue;
                    path.Add(i.ToString());
                    var (ok, converted) = Convert(items[i], node.Element!, path, tier, source, null);
                    path.RemoveAt(path.Count - 1);
                    if (ok && converted is not null) list.Add(converted);
                }
                return (true, list);

            default:
                return Scalar(value, node, path, tier, source);
        }
    }

    private (bool Ok, JsonNode? Node) Scalar(JsonNode value, KeyNode node, List<string> path, SettingsTier tier, string source)
    {
        if (value is not JsonValue jv)
            return Fail("type_mismatch", $"expected {ScalarConverter.Describe(node)}", path, tier, source);

        var type = node.ScalarType!;
        JsonNode? result;

        if (type == typeof(string))
        {
            result = jv.TryGetValue<string>(out var text) ? JsonValue.Create(text) : JsonValue.Create(jv.ToJsonString().Trim('"'));
        }
        else if (jv.TryGetValue<string>(out var asText))
        {
            if (!ScalarConverter.TryConvert(node, asText, out result, out var error))
                return Fail("invalid_value", $"'{asText}': {error}", path, tier, source);
        }
        else if (type == typeof(bool))
        {
            if (!jv.TryGetValue<bool>(out var flag)) return Fail("type_mismatch", "expected true or false", path, tier, source);
            result = JsonValue.Create(flag);
        }
        else if (node.IsNumeric)
        {
            if (!SettingsJson.TryNumber(jv, out var number)) return Fail("type_mismatch", "expected a number", path, tier, source);
            var integral = type == typeof(int) || type == typeof(long) || type == typeof(short);
            if (integral && number != Math.Floor(number)) return Fail("invalid_value", "expected an integer", path, tier, source);
            result = integral ? JsonValue.Create((long)number) : JsonValue.Create(number);
        }
        else
        {
            return Fail("type_mismatch", $"expected {ScalarConverter.Describe(node)}", path, tier, source);
        }

        if (node.IsNumeric && node.Min is { } min && node.Max is { } max && SettingsJson.TryNumber(result, out var checkedNumber))
        {
            if (checkedNumber < min || checkedNumber > max)
                return Fail("out_of_range", $"{checkedNumber} is outside {min}..{max}", path, tier, source);
        }

        if (node.IsSecret && (IsFileTier(tier) || tier == SettingsTier.Flags))
        {
            if (tier is SettingsTier.User or SettingsTier.Managed && EnvironmentCredential.IsProviderKey(path) &&
                result is JsonValue reference && reference.TryGetValue<string>(out var text) && EnvironmentCredential.TryGetVariable(text, out _))
                return (true, result);
            diags.Warn("secret_rejected", $"'{SettingsPath.Format(path)}' is a secret and was ignored. Provide it through its environment variable or the credential vault.", SettingsPath.Format(path), tier, source);
            return (false, null);
        }

        return (true, result);
    }

    private (bool, JsonNode?) Fail(string code, string message, List<string> path, SettingsTier tier, string source)
    {
        diags.Error(code, $"{SettingsPath.Format(path)}: {message}", SettingsPath.Format(path), tier, source);
        return (false, null);
    }
}
