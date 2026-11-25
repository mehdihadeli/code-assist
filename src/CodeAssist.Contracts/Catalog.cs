namespace CodeAssist.Contracts;

/// <summary>Prices in USD per million tokens. Null when the feed does not publish a value.</summary>
public sealed record CatalogPriceDto(decimal? Input, decimal? Output, decimal? CacheRead, decimal? CacheWrite)
{
    public IReadOnlyList<CatalogPriceTierDto> Tiers { get; init; } = [];
}

public sealed record CatalogPriceTierDto(int AboveTokens, CatalogPriceDto Price);

public sealed record CatalogProviderDto(string Id, string Name, string? Api, IReadOnlyList<string> Env, string? Protocol, string? Doc);

/// <summary>One model from the catalog, as shared by every surface (CLI, web, desktop).</summary>
public sealed record CatalogModelDto(
    string ProviderId,
    string ModelId,
    string Name,
    int? ContextWindow,
    int? MaxOutputTokens,
    bool ToolCalls,
    bool Reasoning,
    IReadOnlyList<string> Input,
    IReadOnlyList<string> Output,
    CatalogPriceDto? Price,
    string? Status)
{
    public int? InputTokenLimit { get; init; }
    public bool? ToolCallSupport { get; init; }
    public bool? Temperature { get; init; }
    public bool? Attachment { get; init; }
    public IReadOnlyList<string> ReasoningEfforts { get; init; } = [];
}

/// <summary>Result of a catalog refresh. Status is the refresh outcome name; Error is set on failure.</summary>
public sealed record CatalogRefreshDto(string Status, int ModelCount, string? Error);
