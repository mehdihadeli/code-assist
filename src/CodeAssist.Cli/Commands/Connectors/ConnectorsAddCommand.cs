using System.ComponentModel;
using System.Text.Json;
using CodeAssist.Cli.Hosting;
using CodeAssist.Cli.Presentation;
using CodeAssist.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Connectors;

public sealed class ConnectorsAddCommand(CliContext cli) : CliCommand<ConnectorsAddCommand.Options>(cli)
{
    public sealed class Options : GlobalSettings
    {
        [CommandArgument(0, "<NAME>")]
        [Description("Connector name. For a process connector put the command after --.")]
        public string Name { get; init; } = "";

        [CommandOption("--url <URL>")]
        [Description("Add an HTTP connector instead of a process. HTTPS is required except on loopback.")]
        public string? Url { get; init; }

        [CommandOption("--env <KEY=VALUE>")]
        [Description("Environment variable for the child process. Repeatable. Do not put secrets here.")]
        public string[] Environment { get; init; } = [];

        [CommandOption("--read-only-tools <NAMES>")]
        [Description("Comma-separated tool names to treat as read-only (fewer approval prompts).")]
        public string? ReadOnlyTools { get; init; }

        [CommandOption("--force")]
        [Description("Replace an existing connector with the same name.")]
        public bool Force { get; init; }
    }

    protected override bool AllowInvalidSettings => true;

    protected override Task<int> RunAsync(CommandContext context, Options options, CancellationToken cancellation)
    {
        var name = options.Name;
        var command = context.Remaining.Raw;

        if (options.Url is null && command.Count == 0)
            throw new UsageException("Give a command after '--' (for example: connectors add files -- npx -y some-server) or pass --url.");
        if (options.Url is not null && command.Count > 0)
            throw new UsageException("Use either --url or a command after '--', not both.");

        var defined = Cli.Snapshot.OriginOf(SettingsPath.Format(["connectors", "servers", name, "transport"])).Effective is not null
            || Cli.Snapshot.Settings.Connectors.Servers.ContainsKey(name);
        if (defined && !options.Force)
            throw new UsageException($"Connector '{name}' already exists. Use --force to replace it.");

        string Key(params string[] tail) => SettingsPath.Format(["connectors", "servers", name, .. tail]);
        var items = new List<(string, string)>();
        if (options.Url is not null)
        {
            items.Add((Key("transport"), "http"));
            items.Add((Key("url"), options.Url));
        }
        else
        {
            items.Add((Key("transport"), "stdio"));
            items.Add((Key("command"), command[0]));
            if (command.Count > 1) items.Add((Key("arguments"), JsonSerializer.Serialize(command.Skip(1))));
        }

        foreach (var pair in options.Environment)
        {
            var split = pair.IndexOf('=');
            if (split <= 0) throw new UsageException($"--env expects KEY=VALUE, got '{pair}'.");
            items.Add((Key("environment", pair[..split]), pair[(split + 1)..]));
        }
        if (options.ReadOnlyTools is not null)
        {
            items.Add((Key("trust"), "readOnlyTools"));
            items.Add((Key("readOnlyTools"), options.ReadOnlyTools));
        }

        if (defined) Cli.Editor.Unset(SettingsPath.Format(["connectors", "servers", name]), EditScope.User);
        var result = Cli.Editor.SetMany(items, EditScope.User);
        var code = EditReport.Print(Cli.Console, result, $"Added connector {name}");
        if (code == ExitCodes.Success)
            Cli.Console.MarkupLine("[grey]Connectors start on first use. Connector tools need approval according to your permission preset and rules.[/]");
        return Task.FromResult(code);
    }
}
