using System.Globalization;
using System.Text.Json.Nodes;

namespace CodeAssist.Configuration;

/// <summary>Converts raw text (environment variables, flags, CLI arguments) to the JSON value a scalar key expects.</summary>
public static class ScalarConverter
{
    public static bool TryConvert(KeyNode node, string raw, out JsonNode? value, out string? error)
    {
        value = null;
        error = null;
        var type = node.ScalarType;
        if (type is null)
        {
            error = "not a scalar key";
            return false;
        }

        if (type == typeof(string))
        {
            value = JsonValue.Create(raw);
            return true;
        }

        if (type == typeof(bool))
        {
            switch (raw.Trim().ToLowerInvariant())
            {
                case "true" or "1" or "yes" or "on": value = JsonValue.Create(true); return true;
                case "false" or "0" or "no" or "off": value = JsonValue.Create(false); return true;
                default: error = "expected true or false"; return false;
            }
        }

        if (type.IsEnum)
        {
            var match = node.EnumNames.FirstOrDefault(n => string.Equals(n, raw.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                error = $"expected one of: {string.Join(", ", node.EnumNames)}";
                return false;
            }
            value = JsonValue.Create(match);
            return true;
        }

        if (type == typeof(int) || type == typeof(long) || type == typeof(short))
        {
            if (long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                value = JsonValue.Create(number);
                return true;
            }
            error = "expected an integer";
            return false;
        }

        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
        {
            if (double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                value = JsonValue.Create(number);
                return true;
            }
            error = "expected a number";
            return false;
        }

        error = $"unsupported type {type.Name}";
        return false;
    }

    public static string Describe(KeyNode node) => node.Kind switch
    {
        KeyKind.Scalar when node.IsEnum => string.Join(" | ", node.EnumNames),
        KeyKind.Scalar when node.ScalarType == typeof(bool) => "true | false",
        KeyKind.Scalar when node.IsNumeric => node.Min is { } min && node.Max is { } max ? $"number {min}..{max}" : "number",
        KeyKind.Scalar => "string",
        KeyKind.List => "list (JSON array or comma-separated)",
        KeyKind.Dictionary => "map (JSON object)",
        _ => "object (JSON)",
    };
}
