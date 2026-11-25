using CodeAssist.Cli.Hosting;
using CodeAssist.Configuration;
using Spectre.Console;

namespace CodeAssist.Cli.Presentation;

/// <summary>Prints the outcome of a settings edit and maps it to an exit code.</summary>
public static class EditReport
{
    public static int Print(IAnsiConsole console, EditResult result, string success)
    {
        if (!result.Success)
        {
            console.MarkupLine($"[red]error[/] {Markup.Escape(result.Error ?? "The edit failed.")}");
            DiagnosticsView.Render(console, result.Diagnostics, errorsOnly: true);
            return ExitCodes.UsageOrConfig;
        }

        console.MarkupLine($"[green]{Markup.Escape(success)}[/] [grey]({Markup.Escape(result.File ?? "")})[/]");
        if (result.Notice is not null) console.MarkupLine($"[yellow]{Markup.Escape(result.Notice)}[/]");
        DiagnosticsView.Render(console, result.Diagnostics);
        return ExitCodes.Success;
    }
}
