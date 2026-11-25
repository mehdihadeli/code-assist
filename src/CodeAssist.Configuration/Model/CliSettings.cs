using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CodeAssist.Configuration;

public enum CliMode
{
    [JsonStringEnumMemberName("auto")] Auto,
    [JsonStringEnumMemberName("line")] Line,
    [JsonStringEnumMemberName("fullscreen")] FullScreen,
}

public enum ConnectMode
{
    [JsonStringEnumMemberName("auto")] Auto,
    [JsonStringEnumMemberName("inproc")] InProc,
    [JsonStringEnumMemberName("url")] Url,
}

public enum OutputFormat
{
    [JsonStringEnumMemberName("text")] Text,
    [JsonStringEnumMemberName("json")] Json,
    [JsonStringEnumMemberName("ndjson")] Ndjson,
}

public enum ThemeMode
{
    [JsonStringEnumMemberName("system")] System,
    [JsonStringEnumMemberName("dark")] Dark,
    [JsonStringEnumMemberName("light")] Light,
}

public enum ColorSetting
{
    [JsonStringEnumMemberName("auto")] Auto,
    [JsonStringEnumMemberName("always")] Always,
    [JsonStringEnumMemberName("never")] Never,
}

public enum UnicodeSetting
{
    [JsonStringEnumMemberName("auto")] Auto,
    [JsonStringEnumMemberName("ascii")] Ascii,
}

public enum IconSetting
{
    [JsonStringEnumMemberName("auto")] Auto,
    [JsonStringEnumMemberName("ascii")] Ascii,
    [JsonStringEnumMemberName("nerd")] Nerd,
}

public enum CursorStyle
{
    [JsonStringEnumMemberName("default")] Default,
    [JsonStringEnumMemberName("block")] Block,
    [JsonStringEnumMemberName("underline")] Underline,
    [JsonStringEnumMemberName("bar")] Bar,
}

public enum SubmitKey
{
    [JsonStringEnumMemberName("enter")] Enter,
    [JsonStringEnumMemberName("ctrl+enter")] CtrlEnter,
}

public enum PasteMode
{
    [JsonStringEnumMemberName("compact")] Compact,
    [JsonStringEnumMemberName("full")] Full,
}

public enum ShowHide
{
    [JsonStringEnumMemberName("show")] Show,
    [JsonStringEnumMemberName("hide")] Hide,
}

public enum ToolDetail
{
    [JsonStringEnumMemberName("collapsed")] Collapsed,
    [JsonStringEnumMemberName("expanded")] Expanded,
    [JsonStringEnumMemberName("hidden")] Hidden,
}

public enum MarkdownMode
{
    [JsonStringEnumMemberName("rendered")] Rendered,
    [JsonStringEnumMemberName("source")] Source,
}

public enum SidebarMode
{
    [JsonStringEnumMemberName("auto")] Auto,
    [JsonStringEnumMemberName("hide")] Hide,
}

public enum UsageDisplay
{
    [JsonStringEnumMemberName("off")] Off,
    [JsonStringEnumMemberName("footer")] Footer,
    [JsonStringEnumMemberName("turn")] Turn,
}

public enum DiffLayout
{
    [JsonStringEnumMemberName("auto")] Auto,
    [JsonStringEnumMemberName("split")] Split,
    [JsonStringEnumMemberName("unified")] Unified,
}

public enum DiffSource
{
    [JsonStringEnumMemberName("turn")] Turn,
    [JsonStringEnumMemberName("working")] Working,
    [JsonStringEnumMemberName("branch")] Branch,
}

public enum GrantChoice
{
    [JsonStringEnumMemberName("once")] Once,
    [JsonStringEnumMemberName("session")] Session,
}

public enum CopyBehavior
{
    [JsonStringEnumMemberName("manual")] Manual,
    [JsonStringEnumMemberName("select")] Select,
}

public enum LocationStart
{
    [JsonStringEnumMemberName("launch")] Launch,
    [JsonStringEnumMemberName("inherit")] Inherit,
}

public enum TabVisibility
{
    [JsonStringEnumMemberName("auto")] Auto,
    [JsonStringEnumMemberName("always")] Always,
    [JsonStringEnumMemberName("never")] Never,
}

public enum TabGrouping
{
    [JsonStringEnumMemberName("workspace")] Workspace,
    [JsonStringEnumMemberName("all")] All,
}

public enum TabOrientation
{
    [JsonStringEnumMemberName("horizontal")] Horizontal,
    [JsonStringEnumMemberName("vertical")] Vertical,
}

/// <summary>Shared presentation defaults for every surface; surface sections override them.</summary>
public sealed class AppearanceSettings
{
    public ThemeSettings Theme { get; set; } = new();
    public bool Animations { get; set; } = true;
}

public sealed class ThemeSettings
{
    [Description("Built-in or custom theme id; 'auto' derives colors from the terminal.")]
    public string Name { get; set; } = "auto";

    public ThemeMode Mode { get; set; } = ThemeMode.System;
}

/// <summary>Terminal client settings (CLI and TUI). User tier only; hot reloaded.</summary>
public sealed class CliSettings
{
    public CliMode Mode { get; set; } = CliMode.Auto;
    public CliConnectSettings Connect { get; set; } = new();

    [Description("Default format for non-interactive runs.")]
    public OutputFormat Output { get; set; } = OutputFormat.Text;

    public ThemeSettings Theme { get; set; } = new();
    public ColorSetting Color { get; set; } = ColorSetting.Auto;
    public UnicodeSetting Unicode { get; set; } = UnicodeSetting.Auto;
    public IconSetting Icons { get; set; } = IconSetting.Auto;
    public bool FontAutoInstall { get; set; } = true;
    public bool Animations { get; set; } = true;
    public CliCursorSettings Cursor { get; set; } = new();
    public CliInputSettings Input { get; set; } = new();
    public CliViewSettings View { get; set; } = new();
    public CliLineSettings Line { get; set; } = new();
    public CliScrollSettings Scroll { get; set; } = new();
    public CliApprovalSettings Approvals { get; set; } = new();
    public CliNotificationSettings Notifications { get; set; } = new();
    public CliTerminalSettings Terminal { get; set; } = new();
    public CliSessionSettings Session { get; set; } = new();
    public CliSessionTabsSettings SessionTabs { get; set; } = new();
    public CliKeySettings Keys { get; set; } = new();

    [Description("Terminal extensions (reserved).")]
    public List<string> Plugins { get; set; } = [];

    [Description("Show what changed after an upgrade the next time the terminal opens.")]
    public bool ReleaseNotes { get; set; } = true;

    public CliDebugSettings Debug { get; set; } = new();

    [Description("Opt-in features by id.")]
    public SettingsMap<bool> Labs { get; set; } = new();
}

public sealed class CliConnectSettings
{
    [Reload(ReloadKind.RestartRequired), Description("auto: use or start the local service; inproc: run the runtime inside the CLI; url: connect to Url.")]
    public ConnectMode Mode { get; set; } = ConnectMode.Auto;

    public string? Url { get; set; }
}

public sealed class CliCursorSettings
{
    public CursorStyle Style { get; set; } = CursorStyle.Default;
}

public sealed class CliInputSettings
{
    public bool Mouse { get; set; } = true;
    public SubmitKey Submit { get; set; } = SubmitKey.Enter;
    public PasteMode Paste { get; set; } = PasteMode.Compact;

    [Description("Command used to compose long prompts; empty uses $VISUAL or $EDITOR.")]
    public string? ExternalEditor { get; set; }

    public CliHistorySettings History { get; set; } = new();
}

public sealed class CliHistorySettings
{
    [Range(0, 100_000)] public int Size { get; set; } = 1000;
}

public sealed class CliViewSettings
{
    public ShowHide Thinking { get; set; } = ShowHide.Hide;
    public ToolDetail Tools { get; set; } = ToolDetail.Collapsed;
    public MarkdownMode Markdown { get; set; } = MarkdownMode.Rendered;
    public SidebarMode Sidebar { get; set; } = SidebarMode.Auto;
    public UsageDisplay Usage { get; set; } = UsageDisplay.Footer;
    public CliDiffSettings Diff { get; set; } = new();
}

public sealed class CliDiffSettings
{
    public DiffLayout Layout { get; set; } = DiffLayout.Auto;
    public DiffSource Source { get; set; } = DiffSource.Turn;
}

/// <summary>Options of the scrollback-friendly line mode (also used by `run` when attached to a terminal).</summary>
public sealed class CliLineSettings
{
    public ShowHide Footer { get; set; } = ShowHide.Show;
    public ShowHide Splash { get; set; } = ShowHide.Show;
    public ShowHide TurnSummary { get; set; } = ShowHide.Show;
    public ShowHide ShellOutput { get; set; } = ShowHide.Hide;
    public string Spinner { get; set; } = "dots";
    public bool Mono { get; set; }
    public bool Replay { get; set; } = true;
    [Range(0, 100_000)] public int ReplayLimit { get; set; } = 200;
}

public sealed class CliScrollSettings
{
    [Range(0.001, 1000)] public double Speed { get; set; } = 3;
}

public sealed class CliApprovalSettings
{
    [Description("Pre-selected grant in approval dialogs. There is deliberately no auto-approve switch: use policy rules.")]
    public GrantChoice DefaultGrant { get; set; } = GrantChoice.Once;

    [Range(0, 86400)] public int TimeoutSeconds { get; set; }
}

public sealed class CliNotificationSettings
{
    public bool Enabled { get; set; }
    public bool OnlyWhenUnfocused { get; set; } = true;
    public bool Sound { get; set; }
}

public sealed class CliTerminalSettings
{
    public bool Title { get; set; } = true;
    public CopyBehavior Copy { get; set; } = OperatingSystem.IsWindows() ? CopyBehavior.Manual : CopyBehavior.Select;
}

public sealed class CliSessionSettings
{
    public LocationStart NewLocation { get; set; } = LocationStart.Launch;
}

public sealed class CliSessionTabsSettings
{
    public TabVisibility Show { get; set; } = TabVisibility.Auto;
    public TabGrouping Group { get; set; } = TabGrouping.Workspace;
    public TabOrientation Orientation { get; set; } = TabOrientation.Horizontal;
}

public sealed class CliKeySettings
{
    [Description("Prefix key for chords written as <leader>q.")]
    public string Leader { get; set; } = "ctrl+k";

    [Range(100, 60_000)] public int LeaderTimeoutMs { get; set; } = 1500;

    [Description("Command id to binding(s); \"none\" disables a command.")]
    public SettingsMap<List<string>> Bindings { get; set; } = new();
}

public sealed class CliDebugSettings
{
    public bool Timing { get; set; }
}
