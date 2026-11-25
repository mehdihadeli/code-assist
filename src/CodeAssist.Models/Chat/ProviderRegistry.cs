using CodeAssist.Models.Catalog;
using CodeAssist.Models.Chat.Providers;
using Microsoft.Extensions.AI;

namespace CodeAssist.Models.Chat;

/// <summary>
/// Finds the adapter for a connection: a named adapter first, then a generic adapter for the wire protocol
/// (custom endpoints and providers described only by the models.dev feed).
/// </summary>
public sealed class ProviderRegistry
{
    private static readonly Dictionary<string, string> Unsupported = new(StringComparer.OrdinalIgnoreCase)
    {
        ["google-vertex-anthropic"] = "Claude on Vertex AI has no official .NET client; use Claude through anthropic or amazon-bedrock.",
        ["github-copilot"] = "GitHub Copilot sign-in (OAuth device flow) is not implemented.",
        ["gitlab"] = "GitLab Duo sign-in is not implemented.",
    };

    private readonly Dictionary<string, ProviderAdapter> _byId;
    private readonly Func<string, CatalogProvider?> _catalog;
    private readonly Func<string, string, CatalogModel?> _models;

    public ProviderRegistry(IEnumerable<ProviderAdapter> adapters, Func<string, CatalogProvider?>? catalog = null,
        Func<string, string, CatalogModel?>? models = null)
    {
        _byId = adapters.ToDictionary(adapter => adapter.Id, StringComparer.OrdinalIgnoreCase);
        _catalog = catalog ?? (_ => null);
        _models = models ?? ((_, _) => null);
    }

    public IReadOnlyCollection<ProviderAdapter> Adapters => _byId.Values;

    public static ProviderRegistry CreateDefault(Func<string, CatalogProvider?>? catalog = null,
        Func<string, string, CatalogModel?>? models = null) => new(
    [
        new OpenAiProvider(), new AnthropicProvider(), new AzureProvider(), new GoogleProvider(), new GoogleVertexProvider(),
        new AmazonBedrockProvider(), new GroqProvider(),
        new MistralProvider(), new DeepSeekProvider(), new XAiProvider(), new OpenRouterProvider(), new CerebrasProvider(),
        new TogetherAiProvider(), new FireworksProvider(), new DeepInfraProvider(), new VeniceProvider(), new MoonshotProvider(),
        new ZaiProvider(), new AlibabaProvider(), new CohereProvider(), new VercelAiGatewayProvider(), new BasetenProvider(),
        new DigitalOceanProvider(), new OllamaProvider(), new LmStudioProvider(), new VllmProvider(),
    ], catalog, models);

    public ProviderAdapter? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Environment variables that may hold the key for a provider: the adapter's, else the feed's.</summary>
    public IReadOnlyList<string> KeyVariables(string id)
    {
        if (Find(id) is { } adapter) return adapter.KeyVariables;
        var entry = _catalog(id);
        return WireFromProtocol(entry?.Protocol) is WireProtocol.Bedrock or WireProtocol.Vertex
            ? GenericAdapter(WireFromProtocol(entry!.Protocol)!.Value).KeyVariables
            : entry?.Env ?? [];
    }

    /// <exception cref="NotSupportedException">The provider cannot be reached; the message says why.</exception>
    public (ProviderAdapter Adapter, ProviderConnection Connection) Resolve(ProviderConnection connection, string? model = null)
    {
        var named = Find(connection.Id);
        if (named is null && Unsupported.TryGetValue(connection.Id, out var reason)) throw new NotSupportedException(reason);

        var entry = _catalog(connection.Id);
        var metadata = model is null ? null : FindModel(connection, model);
        var route = connection.Canonical is null ? metadata?.Provider : null;
        var protocol = route?.Protocol ?? entry?.Protocol;
        var wire = connection.Wire != WireProtocol.Auto ? connection.Wire
            : route?.Protocol is not null ? WireFromProtocol(route.Protocol)
            : named?.Wire ?? (protocol is not null ? WireFromProtocol(protocol) : entry is null ? WireProtocol.OpenAiChat : null);
        if (wire is null)
            throw new NotSupportedException($"Provider '{connection.Id}' has no supported wire protocol '{protocol}'. Configure its protocol explicitly.");

        if (connection.Wire == WireProtocol.Auto && route?.Shape is { } shape)
        {
            if (wire is not (WireProtocol.OpenAiChat or WireProtocol.OpenAiResponses))
                throw new NotSupportedException($"Provider '{connection.Id}' cannot use API shape '{shape}' with '{wire}'.");
            wire = shape switch
            {
                "responses" => WireProtocol.OpenAiResponses,
                "completions" => WireProtocol.OpenAiChat,
                _ => throw new NotSupportedException($"Provider '{connection.Id}' has unsupported API shape '{shape}'."),
            };
        }

        var adapter = named is not null && (named.Wire == wire || named is OpenAiProvider && wire == WireProtocol.OpenAiChat)
            ? named : GenericAdapter(wire.Value);
        var catalogApi = named is null ? entry?.Api : null;
        if (wire is WireProtocol.Vertex or WireProtocol.Bedrock && catalogApi?.Contains("${", StringComparison.Ordinal) == true)
            catalogApi = null;
        var endpoint = connection.Endpoint ?? ParseEndpoint(route?.Api) ?? ParseEndpoint(catalogApi);
        endpoint ??= named?.DefaultEndpoint is { } fallback ? new Uri(fallback) : null;
        endpoint ??= adapter.DefaultEndpoint is { } adapterDefault ? new Uri(adapterDefault) : null;
        if (endpoint is null && wire is not (WireProtocol.AzureOpenAi or WireProtocol.Bedrock or WireProtocol.Vertex))
            throw new NotSupportedException($"Provider '{connection.Id}' has no endpoint. Set one in settings.");
        var maxOutput = metadata?.MaxOutputTokens is > 0 ? Math.Min(connection.MaxOutputTokens, metadata.MaxOutputTokens.Value) : connection.MaxOutputTokens;
        return (adapter, connection with { Wire = wire.Value, Endpoint = endpoint, MaxOutputTokens = maxOutput });
    }

    public IChatClient CreateChatClient(ProviderConnection connection, string model, HttpMessageHandler? innerHandler = null)
    {
        var (adapter, resolved) = Resolve(connection, model);
        var transport = ProviderHttp.Create(resolved, innerHandler);
        try
        {
            var client = adapter.CreateChatClient(resolved, model, transport);
            return new CatalogChatClient(client, model, connection.MaxOutputTokens, name => FindModel(connection, name));
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }

    internal static WireProtocol? WireFromProtocol(string? protocol) => protocol?.ToLowerInvariant() switch
    {
        "chat" or "openai" or "openai-chat" or "openai-compatible" => WireProtocol.OpenAiChat,
        "responses" or "openai-responses" => WireProtocol.OpenAiResponses,
        "anthropic" or "messages" => WireProtocol.Anthropic,
        "azure" => WireProtocol.AzureOpenAi,
        "google" or "gemini" => WireProtocol.Gemini,
        "google-vertex" or "vertex" => WireProtocol.Vertex,
        "amazon-bedrock" or "bedrock" => WireProtocol.Bedrock,
        _ => null,
    };

    private ProviderAdapter GenericAdapter(WireProtocol wire) => wire switch
    {
        WireProtocol.Anthropic => _byId.GetValueOrDefault("anthropic") ?? new AnthropicProvider(),
        WireProtocol.AzureOpenAi => _byId.GetValueOrDefault("azure") ?? new AzureProvider(),
        WireProtocol.OpenAiResponses => _byId.GetValueOrDefault("openai") ?? new OpenAiProvider(),
        WireProtocol.Gemini => _byId.GetValueOrDefault("google") ?? new GoogleProvider(),
        WireProtocol.Bedrock => _byId.GetValueOrDefault("amazon-bedrock") ?? new AmazonBedrockProvider(),
        WireProtocol.Vertex => _byId.GetValueOrDefault("google-vertex") ?? new GoogleVertexProvider(),
        WireProtocol.OpenAiChat => new OpenAiCompatibleProvider("openai-compatible", "OpenAI-compatible", null, []),
        _ => throw new NotSupportedException($"Wire protocol '{wire}' is not supported."),
    };

    public CatalogModel? FindModel(ProviderConnection connection, string model) =>
        connection.ModelMetadata ?? (connection.CatalogModelId is { } catalogId ? _models(connection.Canonical ?? connection.Id, catalogId) : null)
        ?? _models(connection.Canonical ?? connection.Id, model);

    private static Uri? ParseEndpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Contains("${", StringComparison.Ordinal) || !Uri.TryCreate(value, UriKind.Absolute, out var endpoint))
            throw new NotSupportedException("Catalog endpoint is invalid or needs provider-specific configuration. Set an endpoint in settings.");
        return endpoint;
    }
}
