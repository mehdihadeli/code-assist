using System.ComponentModel;

namespace CodeAssist.Configuration;

/// <summary>
/// Root of the settings tree. Property names are the JSON keys (camelCase) and, with "__" separators, the environment variable names.
/// Every top-level section carries its trust class; nested keys inherit it unless they override it.
/// </summary>
public sealed class CodeAssistSettings
{
    [Trust(TrustClass.ManagedOnly), Description("Key paths administrators lock; locked keys are read-only for users and apps.")]
    public List<string> Lock { get; set; } = [];

    [Trust(TrustClass.UserOnly), Description("Display name for exports and attribution.")]
    public string? Username { get; set; }

    [Trust(TrustClass.Sensitive), Description("Model providers by id.")]
    public SettingsMap<ProviderSettings> Providers { get; set; } = new()
    {
        ["default"] = new ProviderSettings { Protocol = ProviderProtocol.OpenAi, Endpoint = "https://api.openai.com/v1/" },
    };

    [Trust(TrustClass.Sensitive), Description("External tool servers (Model Context Protocol) and their limits.")]
    public ConnectorsSettings Connectors { get; set; } = new();

    [Trust(TrustClass.Sensitive), Description("Model catalog: where context windows, prices and capabilities come from.")]
    public CatalogSettings Catalog { get; set; } = new();

    [Trust(TrustClass.Tighten), Description("Agent run limits and defaults.")]
    public AgentSettings Agent { get; set; } = new();

    [Trust(TrustClass.Preference), Description("Roles (personas) by name.")]
    public SettingsMap<RoleSettings> Roles { get; set; } = DefaultRoles();

    [Trust(TrustClass.Tighten), Merge(MergeMode.Rules), Description("Policy rules. A project file may add deny rules only.")]
    public List<RuleSettings> Rules { get; set; } = [];

    [Trust(TrustClass.ManagedOnly), Description("Organization-wide hard policy.")]
    public PolicySettings Policy { get; set; } = new();

    [Trust(TrustClass.Tighten)]
    public ContextSettings Context { get; set; } = new();

    [Trust(TrustClass.Tighten)]
    public ToolSettings Tools { get; set; } = new();

    [Trust(TrustClass.Preference)]
    public WorkspaceSettings Workspace { get; set; } = new();

    [Trust(TrustClass.Preference)]
    public InstructionSettings Instructions { get; set; } = new();

    [Trust(TrustClass.Preference)]
    public SkillSettings Skills { get; set; } = new();

    [Trust(TrustClass.Preference), Description("Reusable slash commands by name.")]
    public SettingsMap<PromptCommandSettings> Commands { get; set; } = new();

    [Trust(TrustClass.Sensitive), Description("Read-only context sources by name.")]
    public SettingsMap<ReferenceSettings> References { get; set; } = new();

    [Trust(TrustClass.Preference)]
    public SnapshotSettings Snapshots { get; set; } = new();

    [Trust(TrustClass.Sensitive), Description("Formatters run after file edits.")]
    public FormattingSettings Formatting { get; set; } = new();

    [Trust(TrustClass.Sensitive), Reload(ReloadKind.RestartRequired)]
    public StorageSettings Storage { get; set; } = new();

    [Trust(TrustClass.Sensitive), Reload(ReloadKind.RestartRequired)]
    public HostSettingsSection Host { get; set; } = new();

    [Trust(TrustClass.Sensitive)]
    public UpdateSettings Updates { get; set; } = new();

    [Trust(TrustClass.Sensitive)]
    public TelemetrySettings Telemetry { get; set; } = new();

    [Trust(TrustClass.Preference)]
    public LoggingSettings Logging { get; set; } = new();

    [Trust(TrustClass.Sensitive), Description("External commands run at lifecycle events.")]
    public HooksSettings Hooks { get; set; } = new();

    [Trust(TrustClass.Sensitive), Description("Restrictions and reserved options for locally installed, digest-approved plugins. Configuration never installs or approves a plugin.")]
    public List<PluginSettings> Plugins { get; set; } = [];

    [Trust(TrustClass.UserOnly), Description("Named plugin registry sources. Registration never downloads, installs or enables content.")]
    public SettingsMap<PluginLibrarySettings> PluginLibraries { get; set; } = new();

    [Trust(TrustClass.Preference)]
    public CacheKeepAliveSettings CacheKeepAlive { get; set; } = new();

    [Trust(TrustClass.Preference)]
    public ApprovalSettings Approvals { get; set; } = new();

    [Trust(TrustClass.Preference), Description("Opt-in features by id.")]
    public SettingsMap<bool> Labs { get; set; } = new();

    [Trust(TrustClass.UserOnly)]
    public AppearanceSettings Appearance { get; set; } = new();

    [Trust(TrustClass.UserOnly)]
    public CliSettings Cli { get; set; } = new();

    private static SettingsMap<RoleSettings> DefaultRoles() => new()
    {
        ["builder"] = new RoleSettings { Description = "Full-access coding role." },
        ["planner"] = new RoleSettings
        {
            Description = "Read-only role that investigates and writes a plan.",
            Tools = new RoleToolSettings { Allow = ["read"] },
        },
        ["reviewer"] = new RoleSettings
        {
            Description = "Read-only reviewer for diffs and risks.",
            Delegatable = true,
            Tools = new RoleToolSettings { Allow = ["read"] },
        },
    };
}
