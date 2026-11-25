using CodeAssist.Configuration;
using Spectre.Console;

namespace CodeAssist.Cli.Presentation;

public static class DiagnosticsView
{
    public static void Render(IAnsiConsole console, IEnumerable<SettingsDiagnostic> diagnostics, bool errorsOnly = false, bool includeInfo = false)
    {
        foreach (var d in diagnostics)
        {
            if (errorsOnly && d.Severity != DiagnosticSeverity.Error) continue;
            if (!includeInfo && d.Severity == DiagnosticSeverity.Info) continue;

            var (label, color) = d.Severity switch
            {
                DiagnosticSeverity.Error => ("error", "red"),
                DiagnosticSeverity.Warning => ("warn ", "yellow"),
                _ => ("info ", "grey"),
            };
            var where = d.Source is null ? "" : $" [grey]({Markup.Escape(d.Source)})[/]";
            console.MarkupLine($"[{color}]{label}[/] {Markup.Escape(d.Message)}{where}");
        }
    }
}
