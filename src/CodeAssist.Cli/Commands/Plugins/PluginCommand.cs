using CodeAssist.Cli.Hosting;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Plugins;

public sealed class PluginCommand(CliContext cli) : CliCommand<PluginCommandSettings>(cli)
{
    protected override async Task<int> RunAsync(CommandContext context, PluginCommandSettings options, CancellationToken cancellation)
    {
        return await Cli.ManageAsync("plugins", context.Name, options, cancellation);
    }
}
