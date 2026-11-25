using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeAssist.Extensions.Plugins;

public sealed record PluginCall
{
    public string Tool { get; init; } = "";
    public JsonElement Input { get; init; }
}

public sealed record PluginSequence
{
    public string? Event { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
    public List<PluginCall> Steps { get; init; } = [];
}

public sealed record PluginService
{
    public string Program { get; init; } = "";
    public List<string> Arguments { get; init; } = [];
    public List<string> Environment { get; init; } = [];
    public Dictionary<string, string> Languages { get; init; } = new(StringComparer.Ordinal);
}

internal static class PluginDefinitions
{
    internal static void Validate(PluginContribution contribution, string text)
    {
        if (contribution.Kind is "hook" or "workflow")
        {
            var sequence = Read<PluginSequence>(text);
            if (sequence.Steps is null || sequence.Steps.Count is < 1 or > 32 || sequence.TimeoutSeconds is < 1 or > 120 ||
                (contribution.Kind == "hook" ? sequence.Event is not ("tool.before" or "tool.after") : sequence.Event is not null) ||
                sequence.Steps.Any(step => step is null || !contribution.Tools.Contains(step.Tool, StringComparer.Ordinal) || step.Input.ValueKind != JsonValueKind.Object))
                throw new InvalidDataException("Plugin sequences need 1..32 explicitly listed tool calls, object inputs, bounded deadlines and a supported hook event.");
        }
        else if (contribution.Kind is "mcp" or "language")
        {
            var service = Read<PluginService>(text);
            if (string.IsNullOrWhiteSpace(service.Program) || service.Program.Length > 1024 || service.Program.Any(char.IsControl) ||
                service.Arguments is null || service.Environment is null || service.Languages is null || service.Arguments.Count > 64 || service.Environment.Count > 64 || service.Languages.Count > 32 ||
                service.Arguments.Any(argument => argument is null || argument.Length > 4096 || argument.Contains('\0')) ||
                service.Environment.Any(name => name is null || !Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant)) ||
                service.Languages.Any(pair => !Regex.IsMatch(pair.Key, "^\\.[a-zA-Z0-9]{1,16}$", RegexOptions.CultureInvariant) || string.IsNullOrWhiteSpace(pair.Value) || pair.Value.Length > 64) ||
                (contribution.Kind == "language" ? service.Languages.Count == 0 : service.Languages.Count != 0))
                throw new InvalidDataException("Plugin services need a bounded program/argument vector, environment variable names only and language mappings for language servers.");
        }
        else throw new InvalidDataException("Unsupported structured plugin contribution.");
    }

    internal static T Read<T>(string text)
    {
        try { return JsonSerializer.Deserialize<T>(text, PluginManifest.Json) ?? throw new InvalidDataException("Plugin definition is empty."); }
        catch (JsonException exception) { throw new InvalidDataException("Plugin definition has an invalid or unknown field.", exception); }
    }
}