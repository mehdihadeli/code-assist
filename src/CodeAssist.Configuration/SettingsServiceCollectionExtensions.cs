using CodeAssist.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

public static class SettingsServiceCollectionExtensions
{
    /// <summary>Registers the settings engine: service, editor, flag overrides, options monitor and (optionally) the file watcher.</summary>
    public static IServiceCollection AddCodeAssistSettings(this IServiceCollection services, bool watchFiles = false, string? workingDirectory = null)
    {
        services.TryAddSingleton<IEnvironmentVariables, ProcessEnvironmentVariables>();
        services.TryAddSingleton<FlagOverrides>();
        services.TryAddSingleton(provider => SettingsLocations.Resolve(
            provider.GetRequiredService<IEnvironmentVariables>().GetAll(),
            workingDirectory ?? Directory.GetCurrentDirectory()));
        services.TryAddSingleton<SettingsService>();
        services.TryAddSingleton<ISettingsProvider>(provider => provider.GetRequiredService<SettingsService>());
        services.TryAddSingleton<SettingsEditor>();
        services.TryAddSingleton<IOptionsMonitor<CodeAssistSettings>, SettingsOptionsMonitor>();
        if (watchFiles) services.AddHostedService<SettingsWatcher>();
        return services;
    }
}
