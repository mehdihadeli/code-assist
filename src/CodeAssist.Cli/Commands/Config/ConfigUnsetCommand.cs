using System.ComponentModel;
using CodeAssist.Cli.Presentation;
using CodeAssist.Configuration;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Config;

public sealed class ConfigUnsetCommand(CliContext cli) : CliCommand<ConfigUnsetCommand.Options>(cli)
{
    public sealed class Options : GlobalSettings
    {
        [CommandArgument(0, "<PATH>")]
        public string Path { get; init; } = "";

        [CommandOption("--scope <SCOPE>")]
        [Description("user (default) or project.")]
        public EditScope Scope { get; init; } = EditScope.User;
    }

    protected override bool AllowInvalidSettings => true;

    protected override Task<int> RunAsync(CommandContext context, Options options, CancellationToken cancellation)
    {
        var result = Cli.Editor.Unset(options.Path, options.Scope);
        return Task.FromResult(EditReport.Print(Cli.Console, result, $"Removed {options.Path}"));
    }
}
