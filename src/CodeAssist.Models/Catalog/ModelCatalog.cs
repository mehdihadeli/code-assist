using System.Text.Json.Nodes;

namespace CodeAssist.Models.Catalog;

/// <summary>Per-million-token pricing for a model. Null members are unknown.</summary>
public sealed record CatalogPrice(decimal? Input, decimal? Output, decimal? CacheRead, decimal? CacheWrite)
{
    public IReadOnlyList<CatalogPriceTier> Tiers { get; init; } = [];
}

public sealed record CatalogPriceTier(int AboveTokens, CatalogPrice Price);

public sealed record CatalogRoute(string? Protocol, string? Api, string? Shape);

/// <summary>Model metadata normalized from a models.dev style feed.</summary>
public sealed record CatalogModel(
    string ProviderId,
    string ModelId,
    string Name,
    int? ContextWindow,
    int? MaxOutputTokens,
    bool? ToolCalls,
    bool Reasoning,
    IReadOnlyList<string> Input,
    IReadOnlyList<string> Output,
    CatalogPrice? Price,
    string? Status)
{
    public int? InputTokenLimit { get; init; }

    public bool? Temperature { get; init; }

    public bool? Attachment { get; init; }

    public IReadOnlyList<string> ReasoningEfforts { get; init; } = [];

    public bool ReasoningToggle { get; init; }
    public int? ReasoningBudgetMin { get; init; }
    public int? ReasoningBudgetMax { get; init; }
    public bool? StructuredOutput { get; init; }
    public string? Family { get; init; }
    public string? ReleaseDate { get; init; }

    public CatalogRoute? Provider { get; init; }
}

/// <summary>
/// A provider entry from the feed. Protocol identifies a wire API, independently of any runtime package.
/// </summary>
public sealed record CatalogProvider(
    string Id,
    string Name,
    string? Api,
    IReadOnlyList<string> Env,
    string? Protocol,
    string? Doc);

/// <summary>Immutable, indexed view of the model catalog. Unknown feed fields are ignored.</summary>
public sealed class ModelCatalog
{
    public static ModelCatalog Empty { get; } = new([]);

    private readonly Dictionary<(string Provider, string Model), CatalogModel> _index = new();
    private readonly Dictionary<string, CatalogProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    public ModelCatalog(IEnumerable<CatalogModel> models, IEnumerable<CatalogProvider>? providers = null)
    {
        foreach (var model in models)
        {
            _index[(model.ProviderId, model.ModelId)] = model;
        }

        foreach (var provider in providers ?? [])
        {
            _providers[provider.Id] = provider;
        }
    }

    public int Count => _index.Count;

    public IReadOnlyCollection<CatalogModel> Models => _index.Values;

    public IReadOnlyCollection<CatalogProvider> Providers => _providers.Values;

    public CatalogProvider? FindProvider(string providerId) =>
        _providers.TryGetValue(providerId, out var provider) ? provider : null;

    public CatalogModel? Find(string providerId, string modelId) =>
        _index.TryGetValue((providerId, modelId), out var model) ? model : null;

    /// <summary>
    /// Parses a models.dev <c>api.json</c> document: a JSON object keyed by provider id,
    /// each with a <c>models</c> object keyed by model id.
    /// </summary>
    public static ModelCatalog Parse(JsonNode? root)
    {
        if (root is not JsonObject providers)
        {
            throw new FormatException("Catalog must be a JSON object keyed by provider id.");
        }

        var models = new List<CatalogModel>();
        var providerEntries = new List<CatalogProvider>();
        foreach (var (providerId, providerNode) in providers)
        {
            if (providerNode is not JsonObject provider) continue;

            providerEntries.Add(new CatalogProvider(
                providerId,
                Text(provider, "name") ?? providerId,
                Text(provider, "api"),
                Strings(provider, "env"),
                Text(provider, "protocol"),
                Text(provider, "doc")));

            if (provider["models"] is not JsonObject modelMap)
            {
                continue;
            }

            foreach (var (modelKey, modelNode) in modelMap)
            {
                if (modelNode is not JsonObject model)
                {
                    continue;
                }

                var limit = model["limit"];
                var capabilities = model["capabilities"] as JsonObject;
                var reasoningControls = capabilities?["reasoning"] as JsonObject;
                models.Add(new CatalogModel(
                    ProviderId: providerId,
                    ModelId: modelKey,
                    Name: Text(model, "name") ?? modelKey,
                    ContextWindow: Integer(limit, "context"),
                    MaxOutputTokens: Integer(limit, "output"),
                    ToolCalls: OptionalFlag(capabilities, "tools") ?? OptionalFlag(model, "tool_call"),
                    Reasoning: OptionalFlag(reasoningControls, "supported") ?? Flag(model, "reasoning"),
                    Input: capabilities?["input"] is JsonArray ? Strings(capabilities, "input") : Strings(model["modalities"], "input"),
                    Output: capabilities?["output"] is JsonArray ? Strings(capabilities, "output") : Strings(model["modalities"], "output"),
                    Price: ParsePrice(model["cost"]),
                    Status: Text(model, "status"))
                {
                    InputTokenLimit = Integer(limit, "input"),
                    Temperature = OptionalFlag(capabilities, "temperature") ?? OptionalFlag(model, "temperature"),
                    Attachment = OptionalFlag(model, "attachment"),
                    StructuredOutput = OptionalFlag(capabilities, "structuredOutput") ?? OptionalFlag(model, "structured_output"),
                    Family = Text(model, "family"),
                    ReleaseDate = Text(model, "release_date") ?? Text(model, "releaseDate"),
                    ReasoningToggle = Flag(reasoningControls, "toggle"),
                    ReasoningBudgetMin = Integer(reasoningControls?["budget"], "min"),
                    ReasoningBudgetMax = Integer(reasoningControls?["budget"], "max"),
                    ReasoningEfforts = reasoningControls?["effort"] is JsonArray ? Strings(reasoningControls, "effort").Where(value => value != "default").Distinct().ToArray() : model["reasoning_options"] is JsonArray reasoning
                        ? reasoning.OfType<JsonObject>().Where(option => Text(option, "type") == "effort")
                            .SelectMany(option => Strings(option, "values")).Where(value => value != "null").Distinct().ToArray()
                        : [],
                    Provider = model["provider"] is JsonObject route
                        ? new CatalogRoute(Text(route, "protocol"), Text(route, "api"), Text(route, "shape"))
                        : null,
                });
            }
        }

        return new ModelCatalog(models, providerEntries);
    }

    private static CatalogPrice? ParsePrice(JsonNode? cost)
    {
        if (cost is not JsonObject)
        {
            return null;
        }

        var tiers = cost["tiers"] is JsonArray items
            ? items.OfType<JsonObject>()
                .Where(item => Text(item["tier"], "type") == "context")
                .Select(item => (Size: Integer(item["tier"], "size"), Price: ParsePrice(item)))
                .Where(item => item.Size is > 0 && item.Price is not null)
                .Select(item => new CatalogPriceTier(item.Size!.Value, item.Price!)).OrderBy(tier => tier.AboveTokens).ToArray()
            : [];
        var price = new CatalogPrice(
            Decimal(cost, "input"),
            Decimal(cost, "output"),
            Decimal(cost, "cache_read"),
            Decimal(cost, "cache_write")) { Tiers = tiers };

        return price is { Input: null, Output: null, CacheRead: null, CacheWrite: null, Tiers.Count: 0 } ? null : price;
    }

    private static string? Text(JsonNode? node, string name) =>
        node is JsonObject obj && obj[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool Flag(JsonNode? node, string name) => OptionalFlag(node, name) == true;

    private static bool? OptionalFlag(JsonNode? node, string name) =>
        node is JsonObject obj && obj[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static int? Integer(JsonNode? node, string name) =>
        node is JsonObject obj && obj[name] is JsonValue value && value.TryGetValue<long>(out var number) && number >= 0
            ? (int)Math.Min(number, int.MaxValue)
            : null;

    private static decimal? Decimal(JsonNode? node, string name) =>
        node is JsonObject obj && obj[name] is JsonValue value && value.TryGetValue<decimal>(out var number) && number >= 0 ? number : null;

    private static IReadOnlyList<string> Strings(JsonNode? node, string name) =>
        node is JsonObject obj && obj[name] is JsonArray array
            ? array.Select(item => item is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
                .OfType<string>()
                .ToArray()
            : [];
}
