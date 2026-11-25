using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeAssist.Configuration;

public sealed record SettingsLoadRequest(
    SettingsLocations Locations,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyDictionary<string, string> Flags);

/// <summary>Resolves every tier into one validated snapshot. Pure with respect to its inputs; reads files only.</summary>
public static class SettingsLoader
{
    public static SettingsSnapshot Load(SettingsLoadRequest request)
    {
        var catalog = KeyCatalog.Default;
        var diags = new DiagnosticBag();
        var files = new List<LoadedFile>();
        var origins = new Dictionary<string, SettingOrigin>(StringComparer.Ordinal);
        var effective = SettingsJson.DefaultsTree();
        var canonicalizer = new Canonicalizer(catalog, diags);

        var managedRaw = ReadRaw(request.Locations.ManagedFile, SettingsTier.Managed, diags, files);
        var locks = new LockSet(ReadLocks(managedRaw));
        var merger = new Merger(catalog, effective, locks, diags, origins);

        Apply(request.Locations.PreferencesFile, SettingsTier.Preferences);
        Apply(request.Locations.UserFile, SettingsTier.User);
        foreach (var project in request.Locations.ProjectFiles()) Apply(project, SettingsTier.Project);

        var env = OverrideTree.FromEnvironment(catalog, request.Environment, diags);
        merger.Merge(canonicalizer.Run(env, SettingsTier.Environment, "environment", effective), SettingsTier.Environment, "environment");

        var flags = OverrideTree.FromFlags(catalog, request.Flags, diags);
        merger.Merge(canonicalizer.Run(flags, SettingsTier.Flags, "command line", effective), SettingsTier.Flags, "command line");

        if (managedRaw is not null)
        {
            LegacyMigrator.Migrate(managedRaw, diags, SettingsTier.Managed, request.Locations.ManagedFile);
            merger.Merge(canonicalizer.Run(managedRaw, SettingsTier.Managed, request.Locations.ManagedFile, effective), SettingsTier.Managed, request.Locations.ManagedFile);
        }

        CodeAssistSettings settings;
        try
        {
            settings = effective.Deserialize<CodeAssistSettings>(SettingsJson.Options) ?? new CodeAssistSettings();
        }
        catch (JsonException ex)
        {
            diags.Error("bind_error", $"Settings could not be bound: {ex.Message}");
            settings = new CodeAssistSettings();
        }

        SettingsValidator.Validate(settings, diags);

        var resolved = JsonSerializer.SerializeToNode(settings, SettingsJson.Options)!.AsObject();
        return new SettingsSnapshot
        {
            Id = Hash(resolved),
            Settings = settings,
            Effective = resolved,
            Origins = origins,
            Diagnostics = diags,
            Files = files,
            Locks = locks,
            Locations = request.Locations,
        };

        void Apply(string path, SettingsTier tier)
        {
            var raw = ReadRaw(path, tier, diags, files);
            if (raw is null) return;
            LegacyMigrator.Migrate(raw, diags, tier, path);
            merger.Merge(canonicalizer.Run(raw, tier, path, effective), tier, path);
        }
    }

    private static JsonObject? ReadRaw(string path, SettingsTier tier, DiagnosticBag diags, List<LoadedFile> files)
    {
        if (!File.Exists(path))
        {
            files.Add(new LoadedFile(tier, path, false));
            return null;
        }
        files.Add(new LoadedFile(tier, path, true));
        try
        {
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
            if (JsonNode.Parse(text, documentOptions: SettingsJson.DocumentOptions) is JsonObject root) return root;
            diags.Error("not_an_object", "The file must contain a JSON object.", null, tier, path);
        }
        catch (JsonException ex)
        {
            diags.Error("parse_error", $"Invalid JSON: {ex.Message}", null, tier, path);
        }
        catch (IOException ex)
        {
            diags.Error("read_error", ex.Message, null, tier, path);
        }
        catch (UnauthorizedAccessException ex)
        {
            diags.Error("read_error", ex.Message, null, tier, path);
        }
        return null;
    }

    private static IEnumerable<string> ReadLocks(JsonObject? managed)
    {
        if (managed is null) return [];
        var key = managed.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, "lock", StringComparison.OrdinalIgnoreCase));
        return key is not null && managed[key] is JsonArray array
            ? array.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : "").Where(s => s.Length > 0).ToArray()
            : [];
    }

    private static string Hash(JsonObject tree)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(tree.ToJsonString()));
        return Convert.ToHexString(bytes, 0, 6).ToLowerInvariant();
    }
}
