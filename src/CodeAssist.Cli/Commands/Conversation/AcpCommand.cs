using System.Text.Json;
using CodeAssist.Cli.Hosting;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Conversation;

public sealed class AcpCommand(CliContext cli, TextReader input) : CliCommand<ConnectionSettings>(cli)
{
    protected override bool AllowInvalidSettings => true;

    protected override async Task<int> RunAsync(CommandContext context, ConnectionSettings settings, CancellationToken cancellation)
    {
        await using var connection = new AcpConnection(Cli, settings.Endpoint, settings.Model, input, Cli.Console.Profile.Out.Writer);
        try
        {
            await connection.RunAsync(cancellation);
            return ExitCodes.Success;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or JsonException)
        {
            await Console.Error.WriteLineAsync("ACP connection closed.");
            return cancellation.IsCancellationRequested ? ExitCodes.Interrupted : ExitCodes.Failure;
        }
    }
}