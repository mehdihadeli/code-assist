using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CodeAssist.Configuration;

public enum PermissionPreset
{
    [JsonStringEnumMemberName("readOnly")] ReadOnly,
    [JsonStringEnumMemberName("ask")] Ask,
    [JsonStringEnumMemberName("acceptEdits")] AcceptEdits,
}

public enum BusyDelivery
{
    /// <summary>Run after the current turn finishes.</summary>
    [JsonStringEnumMemberName("queue")] Queue,
    /// <summary>Inject at the next step boundary of the running turn.</summary>
    [JsonStringEnumMemberName("steer")] Steer,
    /// <summary>Refuse while busy (useful for scripts).</summary>
    [JsonStringEnumMemberName("reject")] Reject,
}

public sealed class AgentSettings
{
    [Description("Default model reference: <provider>/<model>[@variant]; a bare model name uses the provider 'default'.")]
    public string Model { get; set; } = "default/gpt-4.1";

    [Description("Role used when a session does not pick one.")]
    public string Role { get; set; } = "builder";

    [Strict(StrictDirection.Order), StrictOrder("readOnly", "ask", "acceptEdits"), Description("Preset that expands to policy rules. A project file may only move toward readOnly.")]
    public PermissionPreset PermissionPreset { get; set; } = PermissionPreset.Ask;

    [Range(1, 1000), Strict(StrictDirection.Min), Description("Maximum model calls per turn.")]
    public int MaxTurns { get; set; } = 20;

    [Range(1, 100_000_000), Strict(StrictDirection.Min), Description("Token budget per run, checked between requests.")]
    public long MaxTokens { get; set; } = 200_000;

    [Range(1, 1000), Strict(StrictDirection.Min)]
    public int MaxToolCallsPerTurn { get; set; } = 16;

    [Range(100, 10_000_000), Strict(StrictDirection.Min)]
    public int MaxToolOutputCharacters { get; set; } = 24_000;

    [Range(1, 64), Strict(StrictDirection.Min), Description("Read-only tools that may run concurrently.")]
    public int MaxParallelTools { get; set; } = 4;

    [Range(0, 1_000_000), Strict(StrictDirection.Min), Description("Optional cost ceiling in USD for a delegation tree.")]
    public decimal? MaxCostUsd { get; set; }

    [Range(0, 8), Strict(StrictDirection.Min), Description("How deep delegated sessions may delegate again.")]
    public int MaxDelegationDepth { get; set; } = 1;

    [Description("What happens to a prompt sent while a turn is running.")]
    public BusyDelivery BusyDelivery { get; set; } = BusyDelivery.Queue;

    [Description("Cheap model for titles and summaries.")]
    public string? SmallModel { get; set; }

    [Description("Models tried in order when the primary is overloaded or unavailable.")]
    public List<string> FallbackModels { get; set; } = [];

    public RetrySettings Retry { get; set; } = new();
}

public sealed class RetrySettings
{
    [Range(1, 20), Strict(StrictDirection.Min)] public int MaxAttempts { get; set; } = 6;
    [Range(100, 600_000)] public int BaseDelayMs { get; set; } = 1000;
    [Range(100, 3_600_000)] public int MaxDelayMs { get; set; } = 30_000;
    [Range(0, 1)] public double Jitter { get; set; } = 0.25;
}

/// <summary>A persona with its own instructions, model preference, tool access and limits.</summary>
public sealed class RoleSettings
{
    public string? Description { get; set; }

    [Description("Can be started by other sessions through delegation.")]
    public bool Delegatable { get; set; }

    [Description("Hidden roles do not appear in pickers.")]
    public bool Hidden { get; set; }

    public string? Model { get; set; }
    public string? SmallModel { get; set; }

    [Description("Instructions appended to the system prompt for this role.")]
    public string? Prompt { get; set; }

    [Range(1, 1000)] public int? MaxTurns { get; set; }
    public string? Color { get; set; }
    public RoleToolSettings Tools { get; set; } = new();
}

public sealed class RoleToolSettings
{
    [Strict(StrictDirection.Subset), Description("Capability tags (read, write, exec, net, connector:<name>) or tool names. Empty means everything. A project file may only shrink it.")]
    public List<string> Allow { get; set; } = [];

    [Merge(MergeMode.Append)]
    public List<string> Deny { get; set; } = [];
}

public enum RuleDecision
{
    [JsonStringEnumMemberName("allow")] Allow,
    [JsonStringEnumMemberName("ask")] Ask,
    [JsonStringEnumMemberName("deny")] Deny,
}

public enum GrantLifetime
{
    [JsonStringEnumMemberName("once")] Once,
    [JsonStringEnumMemberName("session")] Session,
    [JsonStringEnumMemberName("project")] Project,
    [JsonStringEnumMemberName("persistent")] Persistent,
}

/// <summary>A typed policy rule: a capability plus matchers. Evaluated by the policy engine: deny wins, then highest tier, then most specific, then latest.</summary>
public sealed class RuleSettings
{
    [Description("Stable id shown by policy explain.")]
    public string? Id { get; set; }

    [Description("fs.read, fs.write, fs.delete, proc.exec, net.fetch, mcp.call or delegate.")]
    public string Capability { get; set; } = "";

    [Description("Capability-specific matchers: path, command, cwd, host, server, tool, role (globs).")]
    public SettingsMap<string> Match { get; set; } = new();

    public RuleDecision Decision { get; set; } = RuleDecision.Ask;
    public GrantLifetime Lifetime { get; set; } = GrantLifetime.Session;
    public string? Note { get; set; }

    [Description("Tier that contributed the rule (set by the loader).")]
    public string? Origin { get; set; }
}

public sealed class PolicySettings
{
    public SkillGovernanceSettings Skills { get; set; } = new();

    [Description("Hard allow/deny statements about providers, tools, connectors, skills and plugins. Managed tier only.")]
    public List<PolicyStatement> Rules { get; set; } = [];

    public PluginGovernanceSettings Plugins { get; set; } = new();
}

public sealed class PolicyStatement
{
    [Description("provider.use, tool.use, connector.use, skill.use or plugin.use.")]
    public string Action { get; set; } = "";

    [Description("Glob over the id (for tool.use: tool name or capability tag).")]
    public string Resource { get; set; } = "*";

    public RuleDecision Effect { get; set; } = RuleDecision.Deny;
}
