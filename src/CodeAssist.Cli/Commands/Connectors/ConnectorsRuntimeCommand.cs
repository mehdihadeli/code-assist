using System.ComponentModel;
using CodeAssist.Cli.Hosting;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Connectors;

public sealed class ConnectorsRuntimeSettings : OutputSettings
{
    [CommandArgument(0, "<NAME>"), Description("Enabled configured connector name.")]
    public string Name { get; init; } = "";
    [CommandArgument(1, "[PROMPT]"), Description("Optional prompt name; omit to list prompts.")]
    public string? Prompt { get; init; }
    [CommandOption("--arg <NAME=VALUE>"), Description("Repeatable prompt argument. Values are data, never shell commands.")]
    public string[] Arguments { get; init; } = [];
}

public sealed class ConnectorsRuntimeCommand(CliContext cli) : CliCommand<ConnectorsRuntimeSettings>(cli)
{
    protected override async Task<int> RunAsync(CommandContext command, ConnectorsRuntimeSettings options, CancellationToken cancellation)
    {
        return await Cli.ManageAsync("connectors", command.Name, options, cancellation, alwaysJson: true);
    }
}
