using CodeAssist.Testing;
using System.Text.Json.Nodes;
using CodeAssist.Cli.Hosting;
using CodeAssist.Cli.Fonts;
using CodeAssist.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Testing;

namespace CodeAssist.Cli.IntegrationTests.Support;

/// <summary>Runs the real command tree against a temp home, a fake environment and a test console.</summary>
public sealed class CliHarness : IDisposable
{
    public SettingsSandbox Files { get; } = new();
    public Dictionary<string, string> Environment { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string UserFile => Path.Combine(Files.Home, "settings.json");
    public TextReader? Input { get; set; }
    public Action<TestConsole>? ConfigureConsole { get; set; }
    public ITerminalFontSetup FontSetup { get; set; } = Substitute.For<ITerminalFontSetup>();

    public async Task<(int Code, string Output)> Run(params string[] args)
    {
        var console = new TestConsole();
        console.Profile.Width = 240;
        console.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;
        ConfigureConsole?.Invoke(console);

        var services = new ServiceCollection();
        services.AddSingleton<IEnvironmentVariables>(new DictionaryEnvironmentVariables(Environment));
        services.AddSingleton(Files.Locations);
        services.AddSingleton<IAnsiConsole>(console);
        services.AddCodeAssistCli();
        services.AddSingleton(FontSetup);
        if (Input is { } input) services.AddSingleton(input);

        await using var provider = services.BuildServiceProvider();
        var code = await CliApplication.RunAsync(args, provider);
        return (code, console.Output);
    }

    public JsonNode UserJson() => JsonNode.Parse(File.ReadAllText(UserFile), documentOptions: SettingsJson.DocumentOptions)!;

    public void Dispose() => Files.Dispose();
}

