using CodeAssist.Cli.Hosting;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Server;

public sealed class ServeCommand(CliContext cli) : CliCommand<ServeSettings>(cli)
{
    protected override async Task<int> RunAsync(CommandContext context, ServeSettings options, CancellationToken cancellation)
    {
        var settings = Cli.Snapshot.Settings;
        var http = settings.Host.Listen.Http;
        if (string.IsNullOrWhiteSpace(http.Token) || http.Token.Length < 32 || http.Token.Length > 512)
            throw new UsageException("Set Host__Listen__Http__Token to a secret between 32 and 512 characters before starting the server.");
        await using var server = await Cli.LaunchServerAsync(options.Address ?? http.Address, http.Token, cancellation);
        Cli.Console.WriteLine($"Listening on {server.Endpoint.GetLeftPart(UriPartial.Authority)}");
        await server.WaitForExitAsync(cancellation);
        return ExitCodes.Success;
    }
}