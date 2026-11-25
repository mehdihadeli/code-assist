using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace CodeAssist.Configuration;

public sealed class HostedConnectorsSettings
{
    public bool Enabled { get; set; }
    [Description("Explicitly accept provider-executed tools outside the local dispatcher and the provider's separate data-retention policy.")]
    public bool AcceptRemoteExecution { get; set; }
    public SettingsMap<HostedConnectorSettings> Servers { get; set; } = new();
}

public sealed class HostedConnectorSettings
{
    public string Url { get; set; } = "";
    [Secret, Description("Complete environment reference to an already acquired bearer token. The provider does not perform OAuth login or refresh for this token.")]
    public string? Token { get; set; }
    public bool DefaultEnabled { get; set; } = true;
    public bool Deferred { get; set; }
    public SettingsMap<HostedToolRule> ToolRules { get; set; } = new();
    [Range(0, 3600), Description("Toolset cache breakpoint lifetime: zero disables, 300 selects five minutes, 3600 selects one hour.")]
    public int CacheSeconds { get; set; }
    public List<HostedToolSnapshot> FixedTools { get; set; } = [];
}

public sealed class HostedToolRule
{
    public bool? Enabled { get; set; }
    public bool? Deferred { get; set; }
}

public sealed class HostedToolSnapshot
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Schema { get; set; } = "{\"type\":\"object\"}";
}