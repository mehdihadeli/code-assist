using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Conversation;

public class ConnectionSettings : OutputSettings
{
    [CommandOption("--endpoint <ORIGIN>"), Description("Attach to a loopback service using Host__Listen__Http__Token from the environment.")]
    public Uri? Endpoint { get; init; }

    public override ValidationResult Validate() => Endpoint is not null &&
        (Overrides.Length != 0 || Role is not null || PermissionPreset is not null || MaxTurns is not null)
        ? ValidationResult.Error("An attached service retains its startup settings. Omit --set, --role, --permission-preset and --max-turns, or configure the service before starting it.")
        : base.Validate();
}