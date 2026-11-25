using System.ComponentModel;
using CodeAssist.Cli.Presentation;
using CodeAssist.Configuration;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Connectors;

public sealed class ConnectorsRemoveCommand(CliContext cli) : CliCommand<ConnectorsRemoveCommand.Options>(cli)
{
    public sealed class Options : GlobalSettings
    {
        [CommandArgument(0, "<NAME>")]
        [Description("Connector to remove from the user settings file.")]
        public string Name { get; init; } = "";
    }

    protected override bool AllowInvalidSettings => true;

    protected override Task<int> RunAsync(CommandContext context, Options options, CancellationToken cancellation)
    {
        var result = Cli.Editor.Unset(SettingsPath.Format(["connectors", "servers", options.Name]), EditScope.User);
        return Task.FromResult(EditReport.Print(Cli.Console, result, $"Removed connector {options.Name}"));
    }
}
