using System.ComponentModel;
using CodeAssist.Cli.Hosting;
using CodeAssist.Cli.Presentation;
using CodeAssist.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Config;

public sealed class ConfigGetCommand(CliContext cli) : CliCommand<ConfigGetCommand.Options>(cli)
{
    public sealed class Options : OutputSettings
    {
        [CommandArgument(0, "<PATH>")]
        [Description("Setting path, for example agent.maxTurns or providers.default.endpoint.")]
        public string Path { get; init; } = "";

        [CommandOption("--origin")]
        [Description("Also show every tier that offered a value and what happened to it.")]
        public bool Origin { get; init; }
    }

    protected override bool AllowInvalidSettings => true;

    protected override Task<int> RunAsync(CommandContext context, Options options, CancellationToken cancellation)
    {
        if (!PathArgument.TryResolve(options.Path, out var resolved, out var error)) throw new UsageException(error!);

        var snapshot = Cli.Snapshot;
        var value = SettingsSnapshot.RedactValue(snapshot.Find(resolved!.Segments), resolved.Node);

        if (Cli.WantsJson(options.Json)) Cli.WriteJson(value);
        else if (value is null) Cli.Console.Profile.Out.Writer.WriteLine();
        else Cli.Console.Profile.Out.Writer.WriteLine(SettingsView.Display(value));

        if (options.Origin && !Cli.WantsJson(options.Json))
            SettingsView.RenderCandidates(Cli.Console, snapshot.OriginOf(resolved.Text));

        return Task.FromResult(value is null ? ExitCodes.Failure : ExitCodes.Success);
    }
}
