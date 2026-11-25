using CodeAssist.Models;
using CodeAssist.Contracts;

namespace CodeAssist.Models.Catalog;

/// <summary>
/// Serves the cached catalog and refreshes it on demand. The index is swapped as a whole, so readers never see a partial update.
/// </summary>
public sealed class ModelCatalogService : IModelCatalog
{
    private readonly ModelCatalogClient _client;
    private readonly string _cacheDirectory;
    private readonly Func<string> _url;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private volatile ModelCatalog _catalog;

    public ModelCatalogService(ModelCatalogClient client, string cacheDirectory, Func<string> url)
    {
        _client = client;
        _cacheDirectory = cacheDirectory;
        _url = url;
        var load = ModelCatalogClient.LoadCache(cacheDirectory);
        _catalog = load.Catalog;
        LoadWarning = load.Warning;
    }

    /// <summary>Set when the cached feed existed but could not be read.</summary>
    public string? LoadWarning { get; }

    public IReadOnlyList<CatalogProviderDto> ListProviders() => _catalog.Providers
        .OrderBy(provider => provider.Id, StringComparer.OrdinalIgnoreCase)
        .Select(provider => new CatalogProviderDto(provider.Id, provider.Name, provider.Api, provider.Env, provider.Protocol, provider.Doc))
        .ToArray();

    public IReadOnlyList<CatalogModelDto> List(string? providerId = null) =>
        _catalog.Models
            .Where(m => providerId is null || string.Equals(m.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
            .Where(m => !string.Equals(m.Status, "deprecated", StringComparison.OrdinalIgnoreCase))
            .Where(m => m.Output.Count == 0 || m.Output.Contains("text", StringComparer.OrdinalIgnoreCase))
            .OrderBy(m => m.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase)
            .Select(ToDto)
            .ToArray();

    public CatalogModelDto? Find(string providerId, string modelId) =>
        _catalog.Find(providerId, modelId) is { } model ? ToDto(model) : null;

    /// <summary>Provider entry from the feed, or null when unknown or the feed is not loaded.</summary>
    public CatalogProvider? FindProvider(string providerId) => _catalog.FindProvider(providerId);

    public CatalogModel? FindModel(string providerId, string modelId) => _catalog.Find(providerId, modelId);

    public async Task<CatalogRefreshDto> RefreshAsync(CancellationToken cancellationToken)
    {
        await _refresh.WaitAsync(cancellationToken);
        try
        {
            var result = await _client.RefreshAsync(_url(), _cacheDirectory, cancellationToken);
            if (result.Status == CatalogRefreshStatus.Updated)
                _catalog = ModelCatalogClient.LoadCache(_cacheDirectory).Catalog;
            return new CatalogRefreshDto(result.Status.ToString(), result.ModelCount, result.Error);
        }
        finally { _refresh.Release(); }
    }

    private static CatalogModelDto ToDto(CatalogModel model) =>
        new(
            model.ProviderId,
            model.ModelId,
            model.Name,
            model.ContextWindow,
            model.MaxOutputTokens,
            model.ToolCalls ?? false,
            model.Reasoning,
            model.Input,
            model.Output,
            model.Price is null ? null : ToPrice(model.Price),
            model.Status)
        {
            InputTokenLimit = model.InputTokenLimit,
            ToolCallSupport = model.ToolCalls,
            Temperature = model.Temperature,
            Attachment = model.Attachment,
            ReasoningEfforts = model.ReasoningEfforts,
        };

    private static CatalogPriceDto ToPrice(CatalogPrice price) => new(price.Input, price.Output, price.CacheRead, price.CacheWrite)
    {
        Tiers = price.Tiers.Select(tier => new CatalogPriceTierDto(tier.AboveTokens, ToPrice(tier.Price))).ToArray(),
    };
}
