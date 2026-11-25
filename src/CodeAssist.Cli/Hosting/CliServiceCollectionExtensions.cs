using CodeAssist.Cli.Commands;
using CodeAssist.Cli.Fonts;
using CodeAssist.Cli.Hosting;
using CodeAssist.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spectre.Console;

namespace Microsoft.Extensions.DependencyInjection;

public static class CliServiceCollectionExtensions
{
    /// <summary>Registers the CLI: settings engine, console, command context, interceptor and every <see cref="ICommandGroup"/> in this assembly.</summary>
    public static IServiceCollection AddCodeAssistCli(this IServiceCollection services, bool watchSettingsFiles = false)
    {
        services.AddCodeAssistSettings(watchSettingsFiles);

        services.TryAddSingleton<CommandLifetime>();
        services.TryAddSingleton<TextReader>(_ => Console.In);
        services.TryAddSingleton<IAnsiConsole>(provider =>
            ConsoleFactory.Create(provider.GetRequiredService<ISettingsProvider>().Current.Settings.Cli, provider.GetRequiredService<IEnvironmentVariables>()));
        services.TryAddSingleton<IFontFiles, FontFiles>();
        services.TryAddSingleton<IFontSource, HttpFontSource>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(FontPin.Official);
        services.TryAddSingleton(provider => FontPaths.For(
            OperatingSystem.IsWindows() ? FontPlatform.Windows : OperatingSystem.IsMacOS() ? FontPlatform.MacOS : OperatingSystem.IsLinux() ? FontPlatform.Linux : FontPlatform.Unsupported,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            provider.GetRequiredService<IEnvironmentVariables>().GetAll().GetValueOrDefault("XDG_DATA_HOME")));
        services.TryAddSingleton<IFontHost>(provider => new FontHost(provider.GetRequiredService<IFontFiles>(), provider.GetRequiredService<FontPaths>(),
            FontHost.IsLocalEnvironment(provider.GetRequiredService<IEnvironmentVariables>().GetAll(),
                Console.IsInputRedirected || Console.IsOutputRedirected, Environment.UserInteractive,
                File.Exists("/.dockerenv") || File.Exists("/run/.containerenv")), FontPin.Official));
        services.TryAddSingleton<IFontPreferences>(provider =>
        {
            var environment = provider.GetRequiredService<IEnvironmentVariables>().GetAll();
            var identity = string.Join("|", new[] { "TERM_PROGRAM", "TERM", "WT_PROFILE_ID" }.Select(key => environment.GetValueOrDefault(key, "")));
            return new FontPreferences(provider.GetRequiredService<IFontFiles>(), provider.GetRequiredService<SettingsLocations>().StateDirectory, identity);
        });
        services.TryAddSingleton<IFontInteraction, SpectreFontInteraction>();
        services.TryAddSingleton<ITerminalFontSetup>(provider => new TerminalFontSetup(provider.GetRequiredService<IFontHost>(),
            provider.GetRequiredService<IFontPreferences>(), provider.GetRequiredService<IFontSource>(),
            provider.GetRequiredService<IFontInteraction>(), null, provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<CliContext>();
        services.TryAddSingleton<GlobalOverridesInterceptor>();
        var groups = typeof(ICommandGroup).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ICommandGroup).IsAssignableFrom(t));
        foreach (var group in groups) services.AddSingleton(typeof(ICommandGroup), group);

        return services;
    }
}
