using CodeAssist.Cli.Hosting;
using CodeAssist.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Config;

public sealed class ConfigPathCommand(CliContext cli) : CliCommand<OutputSettings>(cli)
{
    protected override bool AllowInvalidSettings => true;

    protected override Task<int> RunAsync(CommandContext context, OutputSettings options, CancellationToken cancellation)
    {
        var locations = Cli.Settings.Locations;
        var rows = new List<(string Name, string Path, bool Exists)>
        {
            ("preferences", locations.PreferencesFile, File.Exists(locations.PreferencesFile)),
            ("user", locations.UserFile, File.Exists(locations.UserFile)),
        };
        var projectFiles = locations.ProjectFiles();
        rows.AddRange(projectFiles.Select(f => ("project", f, true)));
        if (projectFiles.Count == 0) rows.Add(("project", locations.ProjectFileForWrite(), false));
        rows.Add(("managed", locations.ManagedFile, File.Exists(locations.ManagedFile)));
        rows.Add(("home", locations.Home, Directory.Exists(locations.Home)));
        rows.Add(("state", locations.StateDirectory, Directory.Exists(locations.StateDirectory)));

        if (Cli.WantsJson(options.Json))
        {
            Cli.WriteJson(rows.Select(r => new { r.Name, r.Path, r.Exists }).ToArray());
            return Task.FromResult(ExitCodes.Success);
        }

        var table = new Table().Border(TableBorder.Rounded).AddColumn("Tier").AddColumn("Path").AddColumn("Exists");
        foreach (var (name, path, exists) in rows)
            table.AddRow(name, Markup.Escape(path), exists ? "[green]yes[/]" : "[grey]no[/]");
        Cli.Console.Write(table);
        Cli.Console.MarkupLine($"[grey]Override the home with {SettingsLocations.HomeVariable} and the managed directory with {SettingsLocations.ManagedVariable}.[/]");
        return Task.FromResult(ExitCodes.Success);
    }
}
