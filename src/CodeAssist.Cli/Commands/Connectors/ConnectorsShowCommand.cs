using System.ComponentModel;
using CodeAssist.Cli.Hosting;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Connectors;

public sealed class ConnectorsShowCommand(CliContext cli) : CliCommand<ConnectorsShowCommand.Options>(cli)
{
    public sealed class Options : OutputSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";
    }

    protected override Task<int> RunAsync(CommandContext context, Options options, CancellationToken cancellation)
    {
        var servers = Cli.Snapshot.Settings.Connectors.Servers;
        var key = servers.Keys.FirstOrDefault(k => string.Equals(k, options.Name, StringComparison.OrdinalIgnoreCase));
        if (key is null)
        {
            Cli.Console.MarkupLine($"[red]error[/] Connector '{Markup.Escape(options.Name)}' is not defined.");
            return Task.FromResult(ExitCodes.Failure);
        }

        var redacted = Cli.Snapshot.Redacted()["connectors"]!["servers"]![key]!;
        if (Cli.WantsJson(options.Json))
        {
            Cli.WriteJson(redacted);
            return Task.FromResult(ExitCodes.Success);
        }

        var server = servers[key];
        var grid = new Grid().AddColumn(new GridColumn().NoWrap()).AddColumn();
        grid.AddRow("[bold]Transport[/]", server.Transport.ToString().ToLowerInvariant());
        grid.AddRow("[bold]Target[/]", Markup.Escape(ConnectorsListCommand.Target(server)));
        grid.AddRow("[bold]Enabled[/]", server.Enabled ? "yes" : "no");
        grid.AddRow("[bold]Trust[/]", server.Trust.ToString());
        if (server.Environment.Count > 0) grid.AddRow("[bold]Environment[/]", Markup.Escape(string.Join(", ", server.Environment.Keys)));
        if (server.Tools.Allow.Count > 0) grid.AddRow("[bold]Only tools[/]", Markup.Escape(string.Join(", ", server.Tools.Allow)));
        if (server.Tools.Deny.Count > 0) grid.AddRow("[bold]Hidden tools[/]", Markup.Escape(string.Join(", ", server.Tools.Deny)));
        Cli.Console.Write(new Panel(grid).Header(Markup.Escape(key)).Border(BoxBorder.Rounded));
        return Task.FromResult(ExitCodes.Success);
    }
}
