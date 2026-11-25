using CodeAssist.Models.Catalog;
using CodeAssist.Configuration;

namespace CodeAssist.Server.Composition;

internal static class ConfiguredModelMetadata
{
    internal static CatalogModel? Resolve(string providerId, string modelId, ProviderSettings provider, Func<string, string, CatalogModel?> lookup)
    {
        provider.Models.TryGetValue(modelId, out var configured);
        var catalogId = provider.Canonical ?? providerId;
        var published = lookup(catalogId, modelId) ?? (configured?.ModelId is { } upstream ? lookup(catalogId, upstream) : null);
        if (configured is null) return published is null ? null : published with { ProviderId = providerId, Provider = provider.Canonical is null ? published.Provider : null };
        var controls = configured.ReasoningControls;
        var price = configured.Cost;
        return new(providerId, modelId, configured.Name ?? published?.Name ?? modelId,
            Minimum(configured.ContextWindow, published?.ContextWindow), Minimum(configured.MaxOutputTokens, published?.MaxOutputTokens),
            published?.ToolCalls == false ? false : configured.Tools, configured.Reasoning || published?.Reasoning == true,
            configured.Input.Select(value => value.ToString().ToLowerInvariant()).ToArray(),
            configured.Output.Select(value => value.ToString().ToLowerInvariant()).ToArray(),
            new CatalogPrice(price.InputPerMillion ?? published?.Price?.Input, price.OutputPerMillion ?? published?.Price?.Output,
                price.CacheReadPerMillion ?? published?.Price?.CacheRead, price.CacheWritePerMillion ?? published?.Price?.CacheWrite) { Tiers = published?.Price?.Tiers ?? [] },
            configured.Deprecated is not null ? "deprecated" : published?.Status)
        {
            InputTokenLimit = Minimum(configured.InputTokenLimit, published?.InputTokenLimit),
            Temperature = published?.Temperature == false ? false : configured.Temperature,
            StructuredOutput = published?.StructuredOutput,
            Family = configured.Family ?? published?.Family,
            ReleaseDate = configured.ReleaseDate ?? published?.ReleaseDate,
            ReasoningEfforts = controls.Efforts.Count > 0 ? controls.Efforts.ToArray() : published?.ReasoningEfforts ?? [],
            ReasoningToggle = controls.Toggle || published?.ReasoningToggle == true,
            ReasoningBudgetMin = controls.BudgetMin ?? published?.ReasoningBudgetMin,
            ReasoningBudgetMax = controls.BudgetMax ?? published?.ReasoningBudgetMax,
            Provider = provider.Canonical is null ? published?.Provider : null,
        };
    }

    private static int? Minimum(int? configured, int? published) => configured is { } first && published is { } second ? Math.Min(first, second) : configured ?? published;
}