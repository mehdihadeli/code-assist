using System.ComponentModel;
using CodeAssist.Cli.Hosting;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Diagnostics;

public sealed class InspectSettings : OutputSettings
{
    [CommandArgument(0, "<SUBJECT>"), Description("paths or roles.")]
    public string Subject { get; init; } = "";
}

public sealed class InspectCommand(CliContext cli) : CliCommand<InspectSettings>(cli)
{
    protected override bool AllowInvalidSettings => true;

    protected override Task<int> RunAsync(CommandContext context, InspectSettings settings, CancellationToken cancellation)
    {
        switch (settings.Subject)
        {
            case "paths":
                var locations = Cli.Settings.Locations;
                Cli.WriteJson(new
                {
                    locations.Home, locations.WorkingDirectory, locations.ManagedDirectory, locations.StateDirectory,
                    locations.UserFile, locations.ManagedFile, locations.PreferencesFile,
                    projectFiles = locations.ProjectFiles(), executableDirectory = AppContext.BaseDirectory,
                });
                break;
            case "roles":
                Cli.WriteJson(Cli.Snapshot.Settings.Roles.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => new
                {
                    id = item.Key, item.Value.Description, item.Value.Model, item.Value.SmallModel, item.Value.Hidden,
                    item.Value.Delegatable, item.Value.MaxTurns, allow = item.Value.Tools.Allow, deny = item.Value.Tools.Deny,
                }).ToArray());
                break;
            default: throw new UsageException("Inspect subject must be paths or roles.");
        }
        return Task.FromResult(ExitCodes.Success);
    }
}