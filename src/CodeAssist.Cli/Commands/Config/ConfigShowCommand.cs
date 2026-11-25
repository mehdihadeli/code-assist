using System.ComponentModel;
using System.Text.Json.Nodes;
using CodeAssist.Cli.Hosting;
using CodeAssist.Cli.Presentation;
using CodeAssist.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Config;

public sealed class ConfigShowCommand(CliContext cli) : CliCommand<ConfigShowCommand.Options>(cli)
{
    public sealed class Options : OutputSettings
    {
        [CommandOption("--section <PATH>")]
        [Description("Only show keys under this path, for example agent or providers.default.")]
        public string? Section { get; init; }

        [CommandOption("--changed")]
        [Description("Only show keys that some tier changed from the built-in default.")]
        public bool Changed { get; init; }

        [CommandOption("--sources")]
        [Description("Show which tier and file supplies each value.")]
        public bool Sources { get; init; }
    }

    protected override bool AllowInvalidSettings => true;

    protected override Task<int> RunAsync(CommandContext context, Options options, CancellationToken cancellation)
    {
        var snapshot = Cli.Snapshot;
        string[] prefix = [];
        if (options.Section is not null)
        {
            if (!PathArgument.TryResolve(options.Section, out var resolved, out var error))
                throw new UsageException(error!);
            prefix = resolved!.Segments;
        }

        if (Cli.WantsJson(options.Json))
        {
            JsonNode? tree = snapshot.Redacted();
            if (prefix.Length > 0) tree = SettingsSnapshot.FindIn(tree, prefix);
            Cli.WriteJson(tree);
            return Task.FromResult(snapshot.IsValid ? ExitCodes.Success : ExitCodes.UsageOrConfig);
        }

        var leaves = snapshot.Leaves()
            .Where(leaf => prefix.Length == 0 || SettingsPath.StartsWith(SettingsPath.Parse(leaf.Path), prefix))
            .Where(leaf => !options.Changed || leaf.Origin.Effective is not null);

        SettingsView.RenderLeaves(Cli.Console, leaves, options.Sources);
        DiagnosticsView.Render(Cli.Console, snapshot.Diagnostics);
        Cli.Console.MarkupLine($"[grey]snapshot {snapshot.Id}[/]");
        return Task.FromResult(snapshot.IsValid ? ExitCodes.Success : ExitCodes.UsageOrConfig);
    }
}
