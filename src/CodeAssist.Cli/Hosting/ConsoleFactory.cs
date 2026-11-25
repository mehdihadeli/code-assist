using System.Text;
using CodeAssist.Configuration;
using Spectre.Console;

namespace CodeAssist.Cli.Hosting;

/// <summary>Creates the console used by every command, honoring cli.color, cli.unicode and NO_COLOR.</summary>
public static class ConsoleFactory
{
    public static IAnsiConsole Create(CliSettings cli, IEnvironmentVariables environment, TextWriter? output = null)
    {
        var variables = environment.GetAll();
        var noColor = cli.Color == ColorSetting.Never || (cli.Color == ColorSetting.Auto && variables.ContainsKey("NO_COLOR"));

        if (cli.Unicode == UnicodeSetting.Auto && !Console.IsOutputRedirected)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (!Console.IsInputRedirected) Console.InputEncoding = Encoding.UTF8;
        }

        var settings = new AnsiConsoleSettings
        {
            ColorSystem = noColor ? ColorSystemSupport.NoColors : ColorSystemSupport.Detect,
            Ansi = cli.Color == ColorSetting.Always ? AnsiSupport.Yes : AnsiSupport.Detect,
            Out = new AnsiConsoleOutput(output ?? Console.Out),
        };

        var console = AnsiConsole.Create(settings);
        if (cli.Unicode == UnicodeSetting.Auto && !Console.IsInputRedirected && !Console.IsOutputRedirected)
            console.Profile.Capabilities.Unicode = true;
        if (cli.Unicode == UnicodeSetting.Ascii) console.Profile.Capabilities.Unicode = false;
        return console;
    }
}

/// <summary>Cancellation for the running command; cancelled on Ctrl+C so commands can stop cleanly and exit with code 130.</summary>
public sealed class CommandLifetime : IDisposable
{
    private readonly CancellationTokenSource _source = new();

    public CommandLifetime()
    {
        Console.CancelKeyPress += OnCancel;
    }

    public CancellationToken Token => _source.Token;

    private void OnCancel(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        _source.Cancel();
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancel;
        _source.Dispose();
    }
}
