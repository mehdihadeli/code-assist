using System.ComponentModel;
using CodeAssist.Cli.Hosting;
using CodeAssist.Cli.Presentation;
using CodeAssist.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Config;

public sealed class ConfigSetCommand(CliContext cli) : CliCommand<ConfigSetCommand.Options>(cli)
{
    public sealed class Options : GlobalSettings
    {
        [CommandArgument(0, "<PATH>")]
        public string Path { get; init; } = "";

        [CommandArgument(1, "<VALUE>")]
        [Description("Scalars as text, lists as a JSON array or comma-separated, objects as JSON.")]
        public string Value { get; init; } = "";

        [CommandOption("--scope <SCOPE>")]
        [Description("user (default) or project.")]
        public EditScope Scope { get; init; } = EditScope.User;
    }

    protected override bool AllowInvalidSettings => true;

    protected override Task<int> RunAsync(CommandContext context, Options options, CancellationToken cancellation)
    {
        var result = Cli.Editor.Set(options.Path, options.Value, options.Scope);
        return Task.FromResult(EditReport.Print(Cli.Console, result, $"Set {options.Path}"));
    }
}
