using System.Text.Json.Nodes;

namespace CodeAssist.Configuration;

/// <summary>Generates a JSON Schema for settings.json from the reflected key catalog (editor completion and validation).</summary>
public static class SettingsSchemaGenerator
{
    public const string Draft = "https://json-schema.org/draft/2020-12/schema";

    public static JsonObject Generate(string? id = null)
    {
        var schema = Describe(KeyCatalog.Default.Root);
        schema["$schema"] = Draft;
        if (id is not null) schema["$id"] = id;
        schema["title"] = "Code Assist settings";
        ((JsonObject)schema["properties"]!)["$schema"] = new JsonObject { ["type"] = "string" };
        return schema;
    }

    private static JsonObject Describe(KeyNode node)
    {
        var schema = new JsonObject();
        switch (node.Kind)
        {
            case KeyKind.Scalar:
                Scalar(schema, node);
                break;
            case KeyKind.Object:
                schema["type"] = "object";
                var properties = new JsonObject();
                foreach (var (name, child) in node.Children) properties[name] = Describe(child);
                schema["properties"] = properties;
                schema["additionalProperties"] = false;
                break;
            case KeyKind.Dictionary:
                schema["type"] = "object";
                schema["additionalProperties"] = Describe(node.Element!);
                break;
            case KeyKind.List:
                schema["type"] = "array";
                schema["items"] = Describe(node.Element!);
                break;
        }

        if (node.Description is { } description) schema["description"] = description;
        schema["x-trust"] = SettingsJson.CamelCase(node.Trust.ToString());
        if (node.Reload == ReloadKind.RestartRequired) schema["x-restartRequired"] = true;
        if (node.IsSecret) schema["x-secret"] = true;
        return schema;
    }

    private static void Scalar(JsonObject schema, KeyNode node)
    {
        var type = node.ScalarType!;
        if (type.IsEnum)
        {
            schema["type"] = "string";
            schema["enum"] = new JsonArray(node.EnumNames.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
        }
        else if (type == typeof(bool)) schema["type"] = "boolean";
        else if (type == typeof(int) || type == typeof(long) || type == typeof(short)) schema["type"] = "integer";
        else if (node.IsNumeric) schema["type"] = "number";
        else schema["type"] = "string";

        if (node.IsNumeric)
        {
            if (node.Min is { } min) schema["minimum"] = min;
            if (node.Max is { } max) schema["maximum"] = max;
        }
    }
}
