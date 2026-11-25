using CodeAssist.Models;
using CodeAssist.Sessions;
using CodeAssist.Models.Catalog;
using CodeAssist.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodeAssist.Server.Composition;

public static class CatalogComposition
{
    /// <summary>Builds the catalog stack. Cache lives under the state directory; the HTTP timeout comes from catalog.timeoutSeconds.</summary>
    public static CatalogApi CreateCatalog(SettingsLocations locations, CatalogSettings settings, HttpMessageHandler? handler = null)
        => new(CreateCatalogService(locations, settings, handler));

    public static ModelCatalogService CreateCatalogService(SettingsLocations locations, CatalogSettings settings, HttpMessageHandler? handler = null)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
        var cacheDirectory = Path.Combine(locations.StateDirectory, "catalog");
        var service = new ModelCatalogService(new ModelCatalogClient(http), cacheDirectory, () => settings.Url);
        return service;
    }

    /// <summary>Registers the catalog API. Requires the settings engine (<c>AddCodeAssistSettings</c>).</summary>
    public static IServiceCollection AddCodeAssistCatalog(this IServiceCollection services)
    {
        services.TryAddSingleton(provider =>
        {
            var locations = provider.GetRequiredService<SettingsService>().Locations;
            var settings = provider.GetRequiredService<ISettingsProvider>().Current.Settings.Catalog;
            return CreateCatalogService(locations, settings);
        });
        services.TryAddSingleton<IModelCatalog>(provider => provider.GetRequiredService<ModelCatalogService>());
        services.TryAddSingleton<CatalogApi>();
        return services;
    }
}
