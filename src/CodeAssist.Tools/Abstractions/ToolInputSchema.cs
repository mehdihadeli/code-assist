using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace CodeAssist.Tools;

public sealed class ToolInputSchema
{
    private readonly JsonSchema _schema;

    public ToolInputSchema(string schema)
    {
        if (Encoding.UTF8.GetByteCount(schema) > 131072) throw new ArgumentException("Tool schema exceeds 128 KiB.");
        using var document = JsonDocument.Parse(schema);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Tool schema must be a JSON object.");
        CheckReferences(document.RootElement);
        _schema = JsonSchema.FromText(schema);
    }

    public void Validate(JsonElement input)
    {
        var options = new EvaluationOptions
        {
            EvaluateAs = SpecVersion.Draft202012,
            RequireFormatValidation = true,
        };
        options.SchemaRegistry.Fetch = _ => throw new ArgumentException("External schema resolution is disabled.");
        if (!_schema.Evaluate(JsonNode.Parse(input.GetRawText()), options).IsValid)
            throw new ToolArgumentException("Arguments do not match the registered tool input schema.");
    }

    private static void CheckReferences(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) CheckReferences(item);
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is "$ref" or "$dynamicRef" or "$recursiveRef" &&
                    (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString()?.StartsWith('#') != true))
                    throw new ArgumentException("Tool schemas support only fragment-local references; bundle referenced definitions first.");
                CheckReferences(property.Value);
            }
        }
    }
}