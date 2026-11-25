using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CodeAssist.Extensions.Plugins;

public sealed record PluginManifest
{
    public const string FileName = "assist-plugin.json";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
    };

    public int Schema { get; init; } = 1;
    public string Id { get; init; } = "";
    public string Release { get; init; } = "";
    public string Summary { get; init; } = "";
    public Dictionary<string, string> Requires { get; init; } = new(StringComparer.Ordinal);
    public List<PluginContribution> Contributions { get; init; } = [];
    public List<string> Assets { get; init; } = [];

    public static PluginManifest Read(string directory)
    {
        var bytes = PluginFiles.Read(directory, FileName, 131_072);
        return Parse(bytes);
    }

    internal static PluginManifest Parse(byte[] bytes)
    {
        PluginManifest manifest;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            PluginFiles.UniqueProperties(document.RootElement);
            manifest = JsonSerializer.Deserialize<PluginManifest>(bytes, Json) ?? throw new InvalidDataException("Plugin manifest is empty.");
        }
        catch (JsonException exception) { throw new InvalidDataException("Plugin manifest is not valid schema-1 JSON.", exception); }
        manifest.Validate();
        return manifest;
    }

    public void Validate()
    {
        if (Schema != 1 || !ValidId(Id) || !ValidRelease(Release) || string.IsNullOrWhiteSpace(Summary) || Summary.Length > 1024)
            throw new InvalidDataException("Plugin needs schema 1, a lowercase dotted id, a stable major.minor.patch release and a summary up to 1024 characters.");
        if (Requires is null || Contributions is null || Assets is null || Requires.Count > 64 || Contributions.Count > 128 || Assets.Count > 256)
            throw new InvalidDataException("Plugin collection limits exceeded.");
        if (Requires.Any(pair => !ValidId(pair.Key) || pair.Key == Id || !ValidRelease(pair.Value)))
            throw new InvalidDataException("Plugin requirements must name other plugin ids at exact stable releases.");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FileName };
        foreach (var contribution in Contributions)
        {
            if (contribution is null || string.IsNullOrEmpty(contribution.Key) || !Regex.IsMatch(contribution.Key, "^[a-z][a-z0-9-]{0,47}$", RegexOptions.CultureInvariant) ||
                !keys.Add(contribution.Key) || !PluginContribution.Kinds.Contains(contribution.Kind) ||
                string.IsNullOrWhiteSpace(contribution.Description) || contribution.Description.Length > 1024 ||
                contribution.Tools is null || contribution.Tools.Count > 64 || contribution.Tools.Any(tool => string.IsNullOrWhiteSpace(tool) || tool.Length > 128))
                throw new InvalidDataException("Plugin contributions need unique keys, a supported kind, a description and bounded tool names.");
            if (contribution.Kind == "role" && contribution.Tools.Count == 0)
                throw new InvalidDataException("Plugin roles must declare their tool restrictions explicitly.");
            PluginFiles.Relative(contribution.Entry);
            if (!paths.Add(contribution.Entry)) throw new InvalidDataException("Plugin file paths must be unique, including case.");
            var text = contribution.Kind is "skill" or "prompt" or "role" or "style";
            if (!contribution.Entry.EndsWith(text ? ".md" : ".json", StringComparison.Ordinal))
                throw new InvalidDataException("Text contributions use .md; structured contributions use .json.");
        }
        foreach (var asset in Assets)
        {
            PluginFiles.Relative(asset);
            if (!paths.Add(asset)) throw new InvalidDataException("Plugin asset paths must be unique.");
        }
    }

    public static bool ValidId(string? value) => value is not null && value.Length <= 128 &&
        Regex.IsMatch(value, "^[a-z][a-z0-9-]{0,31}(\\.[a-z][a-z0-9-]{0,31}){1,3}$", RegexOptions.CultureInvariant);

    public static bool ValidRelease(string? value) => value is not null &&
        Regex.IsMatch(value, "^(0|[1-9][0-9]{0,5})\\.(0|[1-9][0-9]{0,5})\\.(0|[1-9][0-9]{0,5})$", RegexOptions.CultureInvariant);
}

public sealed record PluginContribution
{
    internal static readonly HashSet<string> Kinds = ["skill", "prompt", "role", "workflow", "hook", "mcp", "language", "style"];
    public string Key { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Entry { get; init; } = "";
    public string Description { get; init; } = "";
    public List<string> Tools { get; init; } = [];
}