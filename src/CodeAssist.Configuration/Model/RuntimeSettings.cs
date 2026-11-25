using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CodeAssist.Configuration;

public sealed class ContextSettings
{
    [Range(1000, 100_000_000), Strict(StrictDirection.Min), Description("Character budget for the assembled prompt before elision starts.")]
    public int MaxCharacters { get; set; } = 240_000;

    [Range(1, 1000), Description("Tool outputs older than this many turns are replaced by stubs that point to the stored blob.")]
    public int StubAfterTurns { get; set; } = 6;

    public CompactionSettings Compaction { get; set; } = new();
}

public sealed class CompactionSettings
{
    [Description("Summarize old context automatically when it nears the window.")]
    public bool Auto { get; set; }

    [Range(10, 99), Description("Percent of the model window at which compaction starts.")]
    public int SoftPercent { get; set; } = 80;

    [Range(10, 100), Description("Percent at which compaction is forced.")]
    public int HardPercent { get; set; } = 92;

    [Range(1000, 10_000_000), Description("Recent tokens kept verbatim after a summary.")]
    public int KeepTokens { get; set; } = 15_000;

    [Description("Prepare the summary in the background before it is needed.")]
    public bool Background { get; set; }
}

public sealed class ToolSettings
{
    [Range(1, 1_073_741_824), Strict(StrictDirection.Min)] public int MaxFileBytes { get; set; } = 1_048_576;
    [Range(1, 86400), Strict(StrictDirection.Min)] public int ProcessTimeoutSeconds { get; set; } = 120;
    [Range(100, 100_000_000), Strict(StrictDirection.Min)] public int MaxProcessOutputCharacters { get; set; } = 24_000;
    [Range(1, 10_000_000), Strict(StrictDirection.Min)] public int MaxSearchFiles { get; set; } = 2000;
    [Range(1, 1_000_000), Strict(StrictDirection.Min)] public int MaxSearchResults { get; set; } = 200;

    [Range(200, 1_000_000), Strict(StrictDirection.Min), Description("Estimated tokens of a tool result kept inline; the rest goes to a blob.")]
    public int InlineOutputTokens { get; set; } = 4000;

    [Range(10, 1_000_000), Strict(StrictDirection.Min), Description("Lines of file or page content returned per call.")]
    public int OutputMaxLines { get; set; } = 2000;

    [Range(1024, 104_857_600), Strict(StrictDirection.Min), Description("Bytes of file or page content returned per call.")]
    public int OutputMaxBytes { get; set; } = 51_200;

    [Merge(MergeMode.Append), Description("Tool names that are never offered to the model, for example proc.run.")]
    public List<string> Disabled { get; set; } = [];

    [Range(1, 10_080), Strict(StrictDirection.Min), Description("Hard cap for background processes, in minutes.")]
    public int BackgroundMaxMinutes { get; set; } = 240;

    [Trust(TrustClass.Sensitive), Description("Shell for process tools; empty selects automatically.")]
    public string? Shell { get; set; }

    [Trust(TrustClass.Sensitive), Description("Optional executable paths for named bash and powershell execution profiles.")]
    public Dictionary<string, string> Shells { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [Trust(TrustClass.Sensitive)]
    public ToolEnvironmentSettings Env { get; set; } = new();

    [Trust(TrustClass.Sensitive), Description("Extra program/subcommand pairs the command classifier understands, for example \"mytool deploy\".")]
    public List<string> SubcommandTable { get; set; } = [];

    public WebToolSettings Web { get; set; } = new();
    public MediaSettings Media { get; set; } = new();
}

public sealed class ToolEnvironmentSettings
{
    [Description("Environment variables passed to child processes in addition to the safe defaults.")]
    public List<string> Allow { get; set; } = [];
}

public sealed class WebToolSettings
{
    [Trust(TrustClass.Sensitive), Description("Allow web.fetch to reach loopback, private and link-local addresses.")]
    public bool AllowPrivateHosts { get; set; }

    [Trust(TrustClass.Sensitive), Description("Search provider id; empty disables web.search.")]
    public string? SearchProvider { get; set; }

    [Trust(TrustClass.Sensitive), Description("Search API endpoint. Required for the searxng provider; no endpoint is contacted until a search is authorized.")]
    public string? SearchEndpoint { get; set; }

    [Range(1, 120), Strict(StrictDirection.Min), Description("Default web.fetch timeout in seconds (the tool caps it at this maximum).")]
    public int FetchTimeoutSeconds { get; set; } = 30;

    [Range(1024, 104_857_600), Strict(StrictDirection.Min), Description("Largest response web.fetch reads, in bytes.")]
    public int FetchMaxBytes { get; set; } = 5_242_880;
}

public sealed class MediaSettings
{
    public bool AutoResize { get; set; } = true;
    [Range(64, 16384), Strict(StrictDirection.Min)] public int MaxImageEdge { get; set; } = 1600;
    [Range(1024, 104_857_600), Strict(StrictDirection.Min)] public int MaxImageBytes { get; set; } = 4_194_304;
    [Range(1024, 1_073_741_824), Strict(StrictDirection.Min)] public int MaxTotalBytes { get; set; } = 20_971_520;
}

public sealed class WorkspaceSettings
{
    [Merge(MergeMode.Append), Description("Globs excluded from search, listing and watching.")]
    public List<string> Ignore { get; set; } = ["bin/**", "obj/**", "node_modules/**"];

    [Merge(MergeMode.Append), Description("Globs that tools may never write.")]
    public List<string> Protected { get; set; } = [".git/**"];

    [Description("Watch the workspace for external changes (stale-edit protection, file tree refresh).")]
    public bool Watch { get; set; } = true;

    [Trust(TrustClass.Sensitive), Description("Parent directory for worktrees; relative paths resolve against the project's canonical checkout.")]
    public string? WorktreeDirectory { get; set; }
}

public sealed class InstructionSettings
{
    [Merge(MergeMode.Append), Description("Instruction files or globs relative to the workspace root.")]
    public List<string> Files { get; set; } = ["AGENTS.md", "CODEASSIST.md"];

    [Range(256, 10_485_760), Strict(StrictDirection.Min)] public int MaxBytes { get; set; } = 32_768;
    [Range(256, 10_485_760), Strict(StrictDirection.Min)] public int MaxTotalBytes { get; set; } = 131_072;

    [Trust(TrustClass.Sensitive), Description("HTTPS URLs fetched as additional instructions (content-hash pinned).")]
    public List<string> Urls { get; set; } = [];
}

public sealed class SkillSettings
{
    [Trust(TrustClass.Sensitive), Merge(MergeMode.Append), Description("Explicit skill directories; user/managed only. Project-local default directories are discovered separately.")]
    public List<string> Directories { get; set; } = [".code-assist/skills"];

    [Description("Enable independently authored standard guidance bundles. No tools or models run during discovery.")]
    public bool StandardGuides { get; set; } = true;

    public SettingsMap<SkillPreferenceSettings> Entries { get; set; } = new();

    [Trust(TrustClass.Sensitive), Description("Reserved skill-registry URLs; remote distribution currently uses verified plugin libraries, not this field.")]
    public List<string> Registries { get; set; } = [];

    [Trust(TrustClass.Sensitive), Description("Reserved standalone skill lock path; no separate skill downloader or lock writer is active.")]
    public string Lock { get; set; } = "skills.lock.json";
}

public enum SkillDisplay { Full, Compact, Manual, Hidden }

public sealed class SkillPreferenceSettings
{
    [Strict(StrictDirection.Order), StrictOrder("full", "compact", "manual", "hidden")]
    public SkillDisplay Display { get; set; } = SkillDisplay.Full;
}

public sealed class SkillGovernanceSettings
{
    [Description("Admit locally discovered Markdown skills. False leaves only admitted plugin and standard sources.")]
    public bool LocalFiles { get; set; } = true;

    [Description("Admit standard guidance bundles. Does not grant tool permissions.")]
    public bool StandardGuides { get; set; } = true;
}

public sealed class PromptCommandSettings
{
    public string? Description { get; set; }

    [Description("Prompt template with single-pass {{args}} and {{arg.<name>}} placeholders. File includes and shell expansion are rejected.")]
    public string Template { get; set; } = "";

    public string? Role { get; set; }
    public string? Model { get; set; }
}

public sealed class ReferenceSettings
{
    [Description("Local directory (workspace-relative or absolute).")]
    public string? Path { get; set; }

    [Description("Git repository (owner/name or URL), cloned shallowly into the cache when enabled.")]
    public string? Repository { get; set; }

    public string? Branch { get; set; }
    public string? Description { get; set; }
}

public enum SnapshotMode
{
    [JsonStringEnumMemberName("files")] Files,
    [JsonStringEnumMemberName("git")] Git,
    [JsonStringEnumMemberName("off")] Off,
}

public sealed class SnapshotSettings
{
    [Description("files: per-file checkpoints; git: adds a shadow-repository tree snapshot; off: no checkpoints.")]
    public SnapshotMode Mode { get; set; } = SnapshotMode.Files;
}

public sealed class FormattingSettings
{
    public bool Enabled { get; set; }
    public SettingsMap<FormatterSettings> Tools { get; set; } = new();
}

public sealed class FormatterSettings
{
    [Description("File extensions (with dot) or globs handled by this formatter.")]
    public List<string> Match { get; set; } = [];

    [Description("Executable followed by its arguments; {file} is replaced by the file path.")]
    public List<string> Command { get; set; } = [];

    public bool Enabled { get; set; } = true;
}

public sealed class StorageSettings
{
    [Description("Directory for journals, blobs and checkpoints; empty uses <home>/state.")]
    public string? Directory { get; set; }

    [Range(1, 3650), Description("Days unreferenced blobs are kept.")]
    public int BlobRetentionDays { get; set; } = 30;
}

public sealed class HostSettingsSection
{
    [Range(1, 10_080), Description("Minutes of inactivity before a workspace's services unload.")]
    public int IdleUnloadMinutes { get; set; } = 60;

    [Range(1, 10_080), Description("Minutes of inactivity before the background service exits; empty keeps it running.")]
    public int? IdleShutdownMinutes { get; set; }

    [Range(1, 1024)] public int MaxActiveSessions { get; set; } = 64;
    [Range(1, 64)] public int MaxConcurrentTurns { get; set; } = 4;
    [Range(1, 3600)] public int TurnTimeoutSeconds { get; set; } = 120;
    [Range(1, 300)] public int HeartbeatSeconds { get; set; } = 20;

    [Description("Persist committed conversations and recover stable session identities for this workspace. Requires exclusive store ownership.")]
    public bool RecoverConversations { get; set; } = true;

    public HostListenSettings Listen { get; set; } = new();
}

public sealed class HostListenSettings
{
    [Description("Named pipe or Unix socket path; empty selects a per-user default.")]
    public string? Pipe { get; set; }

    public HostHttpSettings Http { get; set; } = new();
}

public sealed class HostHttpSettings
{
    public bool Enabled { get; set; }

    [Description("host:port to listen on. Loopback only unless TLS is configured.")]
    public string Address { get; set; } = "127.0.0.1:0";

    [Secret, Description("Bearer token for the HTTP server. Environment only: Host__Listen__Http__Token; at least 32 characters.")]
    public string? Token { get; set; }

    [Description("Explicit browser origins allowed to call the HTTP API. Wildcards are not supported.")]
    public List<string> AllowedOrigins { get; set; } = [];
}

public enum UpdateCheck
{
    [JsonStringEnumMemberName("never")] Never,
    [JsonStringEnumMemberName("notify")] Notify,
    [JsonStringEnumMemberName("install")] Install,
}

public sealed class UpdateSettings
{
    [Description("never: no version check; notify: announce new versions; install: download and stage them.")]
    public UpdateCheck Check { get; set; } = UpdateCheck.Notify;
}

public sealed class TelemetrySettings
{
    [Trust(TrustClass.Sensitive), Description("Export metadata-only observability to a trusted OTLP collector. Requires restart.")]
    public bool Enabled { get; set; }
    [Trust(TrustClass.Sensitive), Description("Collector base URL. Defaults to loopback port 4317 for gRPC or 4318 for HTTP/protobuf.")]
    public string? Endpoint { get; set; }
    public TelemetryProtocol Protocol { get; set; } = TelemetryProtocol.HttpProtobuf;
    public bool Traces { get; set; } = true;
    public bool Metrics { get; set; } = true;
    public bool Logs { get; set; } = true;
    [Range(0, 1)] public double TraceSampleRatio { get; set; } = 1;
    [Range(1, 300)] public int ExportTimeoutSeconds { get; set; } = 5;
    [Range(1, 3600)] public int MetricIntervalSeconds { get; set; } = 30;
    [Trust(TrustClass.Sensitive), Description("OTLP header values must be complete environment references. Never store literal credentials.")]
    public SettingsMap<string> Headers { get; set; } = new();
}

public enum TelemetryProtocol
{
    [JsonStringEnumMemberName("grpc")] Grpc,
    [JsonStringEnumMemberName("httpProtobuf")] HttpProtobuf,
}

public enum LogLevelSetting
{
    [JsonStringEnumMemberName("trace")] Trace,
    [JsonStringEnumMemberName("debug")] Debug,
    [JsonStringEnumMemberName("information")] Information,
    [JsonStringEnumMemberName("warning")] Warning,
    [JsonStringEnumMemberName("error")] Error,
    [JsonStringEnumMemberName("none")] None,
}

public sealed class LoggingSettings
{
    public LogLevelSetting Level { get; set; } = LogLevelSetting.Warning;
}

public enum HookFailMode
{
    [JsonStringEnumMemberName("open")] Open,
    [JsonStringEnumMemberName("closed")] Closed,
}

public sealed class HookSettings
{
    [Description("session.start, turn.start, tool.before, tool.after, turn.end or approval.requested.")]
    public string Event { get; set; } = "tool.before";

    public string Command { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public HookFailMode FailMode { get; set; } = HookFailMode.Open;
    public bool Enabled { get; set; } = true;
}

public sealed class HooksSettings
{
    [Range(1, 600)] public int TimeoutSeconds { get; set; } = 10;
    public SettingsMap<HookSettings> Entries { get; set; } = new();
}

public sealed class PluginSettings
{
    [Description("Installed plugin id, for example team.review. This field never executes a package or executable.")]
    public string Package { get; set; } = "";

    public bool Enabled { get; set; } = true;
    public SettingsMap<string> Options { get; set; } = new();
}

public sealed class CacheKeepAliveSettings
{
    [Description("Send periodic tiny requests so provider prompt caches stay warm during active work.")]
    public bool Enabled { get; set; }

    public string? Prompt { get; set; }
    [Range(1, 60)] public int EveryMinutes { get; set; } = 4;
    [Range(1, 600)] public int ForMinutes { get; set; } = 30;
}

public sealed class ApprovalSettings
{
    [Range(0, 86400), Description("Seconds before an unanswered approval is denied; 0 waits indefinitely.")]
    public int TimeoutSeconds { get; set; }
}
