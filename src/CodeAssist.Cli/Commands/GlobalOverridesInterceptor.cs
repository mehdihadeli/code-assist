using CodeAssist.Cli.Hosting;
using CodeAssist.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands;

/// <summary>
/// Turns the shared flags into the settings "flags" tier before the command runs, then reloads. Flags never touch files.
/// </summary>
public sealed class GlobalOverridesInterceptor(SettingsService settings, IAnsiConsole console) : ICommandInterceptor
{
    public void Intercept(CommandContext context, CommandSettings commandSettings)
    {
        if (commandSettings is not GlobalSettings global) return;

        var flags = settings.Flags;
        _ = settings.Current; // load the baseline first so invalid flags are rejected instead of becoming the baseline
        var hadFlags = flags.Any;
        flags.Clear();
        if (global.Model is not null) flags.Set("agent.model", global.Model);
        if (global.Role is not null) flags.Set("agent.role", global.Role);
        if (global.PermissionPreset is not null) flags.Set("agent.permissionPreset", global.PermissionPreset);
        if (global.MaxTurns is { } turns) flags.Set("agent.maxTurns", turns.ToString(System.Globalization.CultureInfo.InvariantCulture));

        foreach (var item in global.Overrides)
        {
            var split = item.IndexOf('=');
            if (split <= 0) throw new UsageException($"--set expects PATH=VALUE, got '{item}'.");
            flags.Set(item[..split].Trim(), item[(split + 1)..]);
        }

        if (hadFlags || flags.Any)
        {
            var result = settings.Reload();
            if (!result.Accepted)
            {
                Presentation.DiagnosticsView.Render(console, result.Candidate.Diagnostics, errorsOnly: true);
                throw new UsageException("The command-line overrides produce invalid settings.");
            }
        }
        if (global.NoColor) console.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;
    }

    public void InterceptResult(CommandContext context, CommandSettings commandSettings, ref int result) { }
}
