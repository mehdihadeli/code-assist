using System.ComponentModel;
using CodeAssist.Cli.Presentation;
using CodeAssist.Configuration;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Connectors;

/// <summary>Turns a connector on or off in the user settings without removing its definition.</summary>
public abstract class ConnectorsToggleCommand(CliContext cli, bool enable) : CliCommand<ConnectorsToggleCommand.Options>(cli)
{
    public sealed class Options : GlobalSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";
    }

    protected override bool AllowInvalidSettings => true;

    protected override Task<int> RunAsync(CommandContext context, Options options, CancellationToken cancellation)
    {
        var path = SettingsPath.Format(["connectors", "servers", options.Name, "enabled"]);
        if (!Cli.Snapshot.Settings.Connectors.Servers.ContainsKey(options.Name))
            throw new Hosting.UsageException($"Connector '{options.Name}' is not defined.");

        var result = Cli.Editor.Set(path, enable ? "true" : "false", EditScope.User);
        return Task.FromResult(EditReport.Print(Cli.Console, result, $"{(enable ? "Enabled" : "Disabled")} connector {options.Name}"));
    }
}

public sealed class ConnectorsEnableCommand(CliContext cli) : ConnectorsToggleCommand(cli, enable: true);

public sealed class ConnectorsDisableCommand(CliContext cli) : ConnectorsToggleCommand(cli, enable: false);
