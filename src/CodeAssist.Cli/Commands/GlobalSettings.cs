using System.ComponentModel;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands;

/// <summary>Flags accepted by every command. They become the "flags" settings tier for this invocation only.</summary>
public class GlobalSettings : CommandSettings
{
    [CommandOption("--set <PATH=VALUE>")]
    [Description("Override any setting for this run, for example --set agent.maxTurns=5. Repeatable.")]
    public string[] Overrides { get; init; } = [];

    [CommandOption("--model <REF>")]
    [Description("Model reference: <provider>/<model>[[@variant]]. Same as --set agent.model=<REF>.")]
    public string? Model { get; init; }

    [CommandOption("--role <NAME>")]
    [Description("Role to use. Same as --set agent.role=<NAME>.")]
    public string? Role { get; init; }

    [CommandOption("--permission-preset <PRESET>")]
    [Description("readOnly, ask or acceptEdits. Same as --set agent.permissionPreset=<PRESET>.")]
    public string? PermissionPreset { get; init; }

    [CommandOption("--max-turns <N>")]
    [Description("Maximum model calls per turn.")]
    public int? MaxTurns { get; init; }

    [CommandOption("--no-color")]
    [Description("Disable colors for this run.")]
    public bool NoColor { get; init; }
}

/// <summary>Settings for commands that can print machine-readable output.</summary>
public class OutputSettings : GlobalSettings
{
    [CommandOption("--json")]
    [Description("Print JSON instead of formatted text.")]
    public bool Json { get; init; }
}
