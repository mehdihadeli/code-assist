using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CodeAssist.Configuration;

public enum ConnectorTransport
{
    [JsonStringEnumMemberName("stdio")] Stdio,
    [JsonStringEnumMemberName("http")] Http,
}

public enum ConnectorTrust
{
    [JsonStringEnumMemberName("standard")] Standard,
    /// <summary>Tools listed in readOnlyTools are treated as read-only (fewer prompts).</summary>
    [JsonStringEnumMemberName("readOnlyTools")] ReadOnlyTools,
}

public enum ConnectorHttpMode { Auto, Streaming, Events }

/// <summary>External tool servers (Model Context Protocol and, later, plugins) the agent can use. User/managed tier only.</summary>
public sealed class ConnectorsSettings
{
    [Trust(TrustClass.Sensitive), Description("Allow configured HTTP MCP endpoints to resolve to private network addresses. Does not disable TLS validation.")]
    public bool AllowPrivateHosts { get; set; }
    [Range(1, 600), Strict(StrictDirection.Min), Description("Seconds to wait for a connector process or connection to become ready.")]
    public int StartupTimeoutSeconds { get; set; } = 30;

    [Range(1, 600), Strict(StrictDirection.Min), Description("Seconds to wait for the connector's tool catalog.")]
    public int CatalogTimeoutSeconds { get; set; } = 30;

    [Range(1, 86400), Strict(StrictDirection.Min), Description("Seconds a single connector tool call may run.")]
    public int CallTimeoutSeconds { get; set; } = 120;

    [Description("Start enabled connectors at host start instead of on first use.")]
    public bool EagerStart { get; set; }

    [Description("Named connector definitions.")]
    public SettingsMap<ConnectorSettings> Servers { get; set; } = new();
}

public sealed class ConnectorSettings
{
    public ConnectorTransport Transport { get; set; } = ConnectorTransport.Stdio;

    [Description("Executable for stdio connectors.")]
    public string? Command { get; set; }

    public List<string> Arguments { get; set; } = [];

    [Description("Extra environment variables for the child process (do not put secrets here; use the environment).")]
    public SettingsMap<string> Environment { get; set; } = new();

    public string? WorkingDirectory { get; set; }

    [Description("Endpoint for http connectors. HTTPS is required except on loopback.")]
    public string? Url { get; set; }
    public ConnectorHttpMode HttpMode { get; set; }
    public bool FreezeCatalog { get; set; }
    [Description("Optional SDK protocol revision. Null negotiates using SDK discovery and fallback.")]
    public string? ProtocolRevision { get; set; }

    public SettingsMap<string> Headers { get; set; } = new();

    public ConnectorOAuthSettings Oauth { get; set; } = new();

    public bool Enabled { get; set; } = true;

    [Range(1, 600)] public int? StartupTimeoutSeconds { get; set; }
    [Range(1, 600)] public int? CatalogTimeoutSeconds { get; set; }
    [Range(1, 86400)] public int? CallTimeoutSeconds { get; set; }

    public ConnectorTrust Trust { get; set; } = ConnectorTrust.Standard;

    [Description("Tool names treated as read-only when trust is readOnlyTools.")]
    public List<string> ReadOnlyTools { get; set; } = [];

    public ConnectorToolFilter Tools { get; set; } = new();
}

public sealed class ConnectorOAuthSettings
{
    public bool Enabled { get; set; } = true;
    public string? ClientId { get; set; }
    [Secret, Description("Complete {env:VARIABLE} reference to a confidential client secret; literal secrets are rejected.")]
    public string? ClientSecret { get; set; }
    public string? ClientMetadataUri { get; set; }
    public string? RedirectUri { get; set; }
    public string? Scope { get; set; }
    public string? Authority { get; set; }

    [Range(1, 65535)] public int? CallbackPort { get; set; }
}

public sealed class ConnectorToolFilter
{
    public bool DefaultEnabled { get; set; } = true;
    public SettingsMap<bool> Entries { get; set; } = new();
    [Description("When non-empty only these tool names are exposed.")]
    public List<string> Allow { get; set; } = [];

    [Description("Tool names that are never exposed.")]
    public List<string> Deny { get; set; } = [];
}
