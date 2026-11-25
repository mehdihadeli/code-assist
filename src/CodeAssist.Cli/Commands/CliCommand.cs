using CodeAssist.Cli.Hosting;
using CodeAssist.Cli.Presentation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands;

/// <summary>
/// Base class for commands. It applies the shared guard (invalid settings stop the command with exit code 2 unless the
/// command opts out) and turns cancellation into exit code 130.
/// </summary>
public abstract class CliCommand<TSettings>(CliContext cli) : AsyncCommand<TSettings> where TSettings : GlobalSettings
{
    protected CliContext Cli { get; } = cli;

    /// <summary>Commands that inspect or repair configuration can run while the settings are invalid.</summary>
    protected virtual bool AllowInvalidSettings => false;

    public sealed override async Task<int> ExecuteAsync(CommandContext context, TSettings settings)
    {
        if (!AllowInvalidSettings && !Cli.Snapshot.IsValid)
        {
            DiagnosticsView.Render(Cli.Console, Cli.Snapshot.Diagnostics, errorsOnly: true);
            Cli.Console.MarkupLine("[grey]Run [bold]code-assist config validate[/] for details or [bold]code-assist config edit[/] to fix the file.[/]");
            return ExitCodes.UsageOrConfig;
        }

        try
        {
            return await RunAsync(context, settings, Cli.Cancellation);
        }
        catch (OperationCanceledException) when (Cli.Cancellation.IsCancellationRequested)
        {
            return ExitCodes.Interrupted;
        }
    }

    protected abstract Task<int> RunAsync(CommandContext context, TSettings settings, CancellationToken cancellation);
}
