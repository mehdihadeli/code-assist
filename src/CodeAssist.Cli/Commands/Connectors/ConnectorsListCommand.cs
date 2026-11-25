using CodeAssist.Cli.Hosting;
using CodeAssist.Cli.Presentation;
using CodeAssist.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Connectors;

public sealed class ConnectorsListCommand(CliContext cli) : CliCommand<OutputSettings>(cli)
{
    protected override Task<int> RunAsync(CommandContext context, OutputSettings options, CancellationToken cancellation)
    {
        var servers = Cli.Snapshot.Settings.Connectors.Servers.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase).ToArray();

        if (Cli.WantsJson(options.Json))
        {
            Cli.WriteJson(servers.Select(s => new
            {
                Name = s.Key,
                Transport = s.Value.Transport.ToString().ToLowerInvariant(),
                Target = Target(s.Value),
                s.Value.Enabled,
                Trust = s.Value.Trust.ToString(),
            }).ToArray());
            return Task.FromResult(ExitCodes.Success);
        }

        if (servers.Length == 0)
        {
            Cli.Console.MarkupLine("[grey]No connectors configured. Add one with [bold]code-assist connectors add <name> -- <command> [[args]][/] or [bold]--url <url>[/].[/]");
            return Task.FromResult(ExitCodes.Success);
        }

        var table = new Table().Border(TableBorder.Rounded).AddColumn("Connector").AddColumn("Transport").AddColumn("Target").AddColumn("Enabled").AddColumn("Trust");
        foreach (var (name, server) in servers)
            table.AddRow(Markup.Escape(name), server.Transport.ToString().ToLowerInvariant(), Markup.Escape(SettingsView.Truncate(Target(server), 60)),
                server.Enabled ? "[green]yes[/]" : "[grey]no[/]", server.Trust.ToString());
        Cli.Console.Write(table);
        return Task.FromResult(ExitCodes.Success);
    }

    internal static string Target(ConnectorSettings server) =>
        server.Transport == ConnectorTransport.Stdio
            ? string.Join(' ', new[] { server.Command ?? "" }.Concat(server.Arguments))
            : server.Url ?? "";
}
