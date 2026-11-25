using CodeAssist.Models;
using CodeAssist.Contracts;

namespace CodeAssist.Sessions;

/// <summary>Transport-agnostic catalog operations. The server and the in-process SDK both bind to these.</summary>
public sealed class CatalogApi(IModelCatalog catalog)
{
    public IReadOnlyList<CatalogProviderDto> ListProviders() => catalog.ListProviders();

    public IReadOnlyList<CatalogModelDto> ListModels(string? providerId = null) => catalog.List(providerId);

    public CatalogModelDto? GetModel(string providerId, string modelId) => catalog.Find(providerId, modelId);

    public Task<CatalogRefreshDto> RefreshAsync(CancellationToken cancellationToken) => catalog.RefreshAsync(cancellationToken);
}
