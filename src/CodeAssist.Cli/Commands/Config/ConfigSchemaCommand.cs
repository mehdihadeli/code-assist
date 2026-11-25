using System.ComponentModel;
using CodeAssist.Cli.Hosting;
using CodeAssist.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Config;

public sealed class ConfigSchemaCommand(CliContext cli) : CliCommand<ConfigSchemaCommand.Options>(cli)
{
    public sealed class Options : GlobalSettings
    {
        [CommandOption("--output <FILE>")]
        [Description("Write the schema to a file instead of standard output.")]
        public string? Output { get; init; }

        [CommandOption("--id <URI>")]
        [Description("Value for the schema's $id.")]
        public string? Id { get; init; }
    }

    protected override bool AllowInvalidSettings => true;

    protected override Task<int> RunAsync(CommandContext context, Options options, CancellationToken cancellation)
    {
        var schema = SettingsSchemaGenerator.Generate(options.Id);
        if (options.Output is null)
        {
            Cli.WriteJson(schema);
            return Task.FromResult(ExitCodes.Success);
        }

        var path = Path.GetFullPath(options.Output);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, schema.ToJsonString(SettingsJson.Indented) + Environment.NewLine);
        Cli.Console.MarkupLine($"[green]Wrote schema[/] [grey]({Markup.Escape(path)})[/]");
        return Task.FromResult(ExitCodes.Success);
    }
}
