using System.Text.Json;

namespace CodeAssist.Tools;

public sealed class ToolArgumentException(IReadOnlyList<string> issues)
    : Exception(FormatIssues(issues))
{
    public const int MaxReported = 5;

    public IReadOnlyList<string> Issues { get; } = issues;

    public ToolArgumentException(string issue) : this([issue]) { }

    private static string FormatIssues(IReadOnlyList<string> issues)
    {
        var shown = issues.Take(MaxReported).ToList();
        if (issues.Count > MaxReported) shown.Add($"and {issues.Count - MaxReported} more.");
        return string.Join(" ", shown);
    }
}

/// <summary>
/// Reads model-supplied JSON. Getters record problems instead of throwing so one call reports several issues;
/// <see cref="Validate"/> throws once with at most five of them.
/// </summary>
public sealed class ToolArguments
{
    private readonly JsonElement _root;
    private readonly List<string> _issues;
    private readonly string _prefix;

    private ToolArguments(JsonElement root, List<string> issues, string prefix)
    {
        _root = root;
        _issues = issues;
        _prefix = prefix;
    }

    public static ToolArguments Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var root = document.RootElement.Clone();
            if (root.ValueKind != JsonValueKind.Object)
                throw new ToolArgumentException("Arguments must be a JSON object.");
            return new ToolArguments(root, [], "");
        }
        catch (JsonException exception)
        {
            throw new ToolArgumentException($"Arguments are not valid JSON: {exception.Message}");
        }
    }

    public bool Has(string name) => _root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null;

    public JsonElement Json => _root;

    public string RequireString(string name)
    {
        var value = String(name);
        if (value is null) Issue($"'{Name(name)}' is required (string).");
        return value ?? "";
    }

    public string? String(string name)
    {
        if (!Has(name)) return null;
        var element = _root.GetProperty(name);
        if (element.ValueKind == JsonValueKind.String) return element.GetString();
        Issue($"'{Name(name)}' must be a string.");
        return null;
    }

    public int? Int(string name, int min = int.MinValue, int max = int.MaxValue)
    {
        if (!Has(name)) return null;
        var element = _root.GetProperty(name);
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) && value >= min && value <= max) return value;
        Issue($"'{Name(name)}' must be an integer" + (min != int.MinValue || max != int.MaxValue ? $" between {min} and {max}." : "."));
        return null;
    }

    public bool Bool(string name, bool fallback = false)
    {
        if (!Has(name)) return fallback;
        var element = _root.GetProperty(name);
        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False) return element.GetBoolean();
        Issue($"'{Name(name)}' must be true or false.");
        return fallback;
    }

    /// <summary>Elements of an array of objects. A missing optional array yields an empty list.</summary>
    public IReadOnlyList<ToolArguments> Objects(string name, bool required = false)
    {
        if (!Has(name))
        {
            if (required) Issue($"'{Name(name)}' is required (array).");
            return [];
        }
        var element = _root.GetProperty(name);
        if (element.ValueKind != JsonValueKind.Array)
        {
            Issue($"'{Name(name)}' must be an array.");
            return [];
        }

        var items = new List<ToolArguments>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object) items.Add(new ToolArguments(item, _issues, $"{Name(name)}[{index}]."));
            else Issue($"'{Name(name)}[{index}]' must be an object.");
            index++;
        }
        return items;
    }

    public void Issue(string message) => _issues.Add(message);

    public void Validate()
    {
        if (_issues.Count > 0) throw new ToolArgumentException(_issues.ToArray());
    }

    private string Name(string name) => _prefix + name;
}
