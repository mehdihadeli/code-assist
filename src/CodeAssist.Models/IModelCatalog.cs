using CodeAssist.Contracts;

namespace CodeAssist.Models;

/// <summary>Read and refresh access to the model catalog. Implementations own caching and network IO.</summary>
public interface IModelCatalog
{
    IReadOnlyList<CatalogProviderDto> ListProviders();

    IReadOnlyList<CatalogModelDto> List(string? providerId = null);

    CatalogModelDto? Find(string providerId, string modelId);

    Task<CatalogRefreshDto> RefreshAsync(CancellationToken cancellationToken);
}
