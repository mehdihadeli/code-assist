using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using CodeAssist.Cli.Hosting;
using CodeAssist.Cli.Presentation;
using CodeAssist.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Config;

public sealed class ConfigEditCommand(CliContext cli) : CliCommand<ConfigEditCommand.Options>(cli)
{
    public sealed class Options : GlobalSettings
    {
        [CommandOption("--scope <SCOPE>")]
        [Description("user (default) or project.")]
        public EditScope Scope { get; init; } = EditScope.User;
    }

    protected override bool AllowInvalidSettings => true;

    protected override async Task<int> RunAsync(CommandContext context, Options options, CancellationToken cancellation)
    {
        var locations = Cli.Settings.Locations;
        var file = options.Scope == EditScope.User ? locations.UserFile : locations.ProjectFileForWrite();
        if (!File.Exists(file))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, "{\n}\n", cancellation);
        }

        var variables = Cli.Environment.GetAll();
        var editor = variables.GetValueOrDefault("VISUAL") is { Length: > 0 } visual ? visual
            : variables.GetValueOrDefault("EDITOR") is { Length: > 0 } fallback ? fallback
            : OperatingSystem.IsWindows() ? "notepad" : "nano";

        var words = Split(editor);
        var start = new ProcessStartInfo(words[0]) { UseShellExecute = false };
        foreach (var word in words.Skip(1)) start.ArgumentList.Add(word);
        start.ArgumentList.Add(file);

        try
        {
            using var process = Process.Start(start) ?? throw new UsageException($"Could not start '{editor}'.");
            await process.WaitForExitAsync(cancellation);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new UsageException($"Could not start '{editor}': {ex.Message}. Set the EDITOR environment variable to your editor command.");
        }

        var result = Cli.Settings.Reload();
        DiagnosticsView.Render(Cli.Console, result.Snapshot.Diagnostics);
        if (!result.Accepted)
        {
            Cli.Console.MarkupLine("[red]The saved file is not valid; the previous settings stay in effect until it is fixed.[/]");
            return ExitCodes.UsageOrConfig;
        }
        Cli.Console.MarkupLine($"[green]Settings reloaded[/] [grey](snapshot {result.Snapshot.Id})[/]");
        return ExitCodes.Success;
    }

    private static List<string> Split(string command)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        foreach (var c in command)
        {
            if (quote != '\0') { if (c == quote) quote = '\0'; else current.Append(c); }
            else if (c is '"' or '\'') quote = c;
            else if (char.IsWhiteSpace(c)) { if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); } }
            else current.Append(c);
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words.Count == 0 ? ["notepad"] : words;
    }
}
