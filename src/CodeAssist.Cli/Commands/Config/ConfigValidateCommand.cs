using System.ComponentModel;
using CodeAssist.Cli.Hosting;
using CodeAssist.Cli.Presentation;
using CodeAssist.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Config;

public sealed class ConfigValidateCommand(CliContext cli) : CliCommand<ConfigValidateCommand.Options>(cli)
{
    public sealed class Options : OutputSettings
    {
        [CommandOption("--strict")]
        [Description("Treat warnings as failures.")]
        public bool Strict { get; init; }
    }

    protected override bool AllowInvalidSettings => true;

    protected override Task<int> RunAsync(CommandContext context, Options options, CancellationToken cancellation)
    {
        var snapshot = Cli.Snapshot;
        var warnings = snapshot.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
        var failed = !snapshot.IsValid || (options.Strict && warnings > 0);

        if (Cli.WantsJson(options.Json))
        {
            Cli.WriteJson(new
            {
                valid = !failed,
                snapshot = snapshot.Id,
                files = snapshot.Files.Where(f => f.Exists).Select(f => new { tier = f.Tier.ToString(), f.Path }),
                diagnostics = snapshot.Diagnostics.Select(d => new { severity = d.Severity.ToString().ToLowerInvariant(), d.Code, d.Message, d.Path, tier = d.Tier?.ToString(), d.Source }),
            });
        }
        else
        {
            DiagnosticsView.Render(Cli.Console, snapshot.Diagnostics, includeInfo: true);
            Cli.Console.MarkupLine(failed
                ? $"[red]Settings are not valid[/] [grey]({warnings} warning(s))[/]"
                : $"[green]Settings are valid[/] [grey](snapshot {snapshot.Id}, {warnings} warning(s))[/]");
        }
        return Task.FromResult(failed ? ExitCodes.UsageOrConfig : ExitCodes.Success);
    }
}
