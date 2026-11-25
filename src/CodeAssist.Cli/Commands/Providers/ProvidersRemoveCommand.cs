using System.ComponentModel;
using CodeAssist.Cli.Presentation;
using CodeAssist.Configuration;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Providers;

public sealed class ProvidersRemoveCommand(CliContext cli) : CliCommand<ProvidersRemoveCommand.Options>(cli)
{
    public sealed class Options : GlobalSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Provider id to remove from the user settings file.")]
        public string Id { get; init; } = "";
    }

    protected override bool AllowInvalidSettings => true;

    protected override Task<int> RunAsync(CommandContext context, Options options, CancellationToken cancellation)
    {
        var result = Cli.Editor.Unset(SettingsPath.Format(["providers", options.Id]), EditScope.User);
        return Task.FromResult(EditReport.Print(Cli.Console, result, $"Removed provider {options.Id}"));
    }
}
