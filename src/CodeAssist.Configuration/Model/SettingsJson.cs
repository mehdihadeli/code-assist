using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CodeAssist.Configuration;

/// <summary>Serialization conventions shared by the loader, the editor and the schema generator.</summary>
public static class SettingsJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };

    public static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string CamelCase(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);

    /// <summary>JSON spellings of an enum, in declaration order.</summary>
    public static IReadOnlyList<string> EnumNames(Type enumType) =>
        enumType.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => f.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? CamelCase(f.Name))
            .ToArray();

    public static JsonObject DefaultsTree() =>
        JsonSerializer.SerializeToNode(new CodeAssistSettings(), Options)!.AsObject();

    public static JsonNode? CloneOrNull(JsonNode? node) => node?.DeepClone();

    /// <summary>Reads any numeric JSON value as a double regardless of how the node stores it.</summary>
    public static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue v) return false;
        if (v.TryGetValue(out value)) return true;
        if (v.TryGetValue<long>(out var asLong)) { value = asLong; return true; }
        if (v.TryGetValue<int>(out var asInt)) { value = asInt; return true; }
        if (v.TryGetValue<decimal>(out var asDecimal)) { value = (double)asDecimal; return true; }
        if (v.TryGetValue<JsonElement>(out var element) && element.ValueKind == JsonValueKind.Number) return element.TryGetDouble(out value);
        return false;
    }
}
