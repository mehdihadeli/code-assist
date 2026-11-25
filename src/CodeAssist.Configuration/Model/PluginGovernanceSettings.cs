using System.ComponentModel;

namespace CodeAssist.Configuration;

public sealed class PluginGovernanceSettings
{
    [Description("Exact registry sources allowed before access. Null is unrestricted; an empty list blocks all registries.")]
    public List<string>? Sources { get; set; }

    [Description("Exact plugin ids admitted. Null is unrestricted; an empty list blocks all plugins.")]
    public List<string>? Ids { get; set; }

    [Description("Plugin ids rejected even when another rule admits them.")]
    public List<string> Reject { get; set; } = [];

    [Description("Required plugin id to reviewed content SHA-256. Must already be installed; no automatic download.")]
    public SettingsMap<string> Required { get; set; } = new();

    [Description("Content digests permitted to contribute hooks or workflows. Null permits approved sequences; empty blocks all.")]
    public List<string>? SequenceDigests { get; set; }

    [Description("Allow direct directory/ZIP installation outside a registry.")]
    public bool LocalPackages { get; set; } = true;
}

public sealed class PluginLibrarySettings
{
    [Description("Local registry path or HTTPS catalog URL; relative paths resolve from the working directory.")]
    public string Source { get; set; } = "";
}