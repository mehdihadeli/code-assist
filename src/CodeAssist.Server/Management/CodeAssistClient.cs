using CodeAssist.Server.Composition;
using CodeAssist.Sessions;
using CodeAssist.Contracts;
using CodeAssist.Configuration;

namespace CodeAssist.Server.Management;

/// <summary>In-process client. Calls the same host operations the server exposes.</summary>
public sealed class CodeAssistClient(CatalogApi catalog, ToolsApi tools)
{
    public static CodeAssistClient Create(SettingsLocations locations, CodeAssistSettings settings) =>
        new(CatalogComposition.CreateCatalog(locations, settings.Catalog), ToolComposition.CreateApi(settings));

    public IReadOnlyList<CatalogModelDto> ListModels(string? providerId = null) => catalog.ListModels(providerId);

    public IReadOnlyList<CatalogProviderDto> ListProviders() => catalog.ListProviders();

    public CatalogModelDto? GetModel(string providerId, string modelId) => catalog.GetModel(providerId, modelId);

    public Task<CatalogRefreshDto> RefreshCatalogAsync(CancellationToken cancellationToken) => catalog.RefreshAsync(cancellationToken);

    public IReadOnlyList<ToolInfoDto> ListTools(IReadOnlyCollection<string>? allow = null, IReadOnlyCollection<string>? deny = null) => tools.List(allow, deny);

    public ToolInfoDto? GetTool(string name) => tools.Get(name);
}
