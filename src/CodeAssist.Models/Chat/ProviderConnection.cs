namespace CodeAssist.Models.Chat;

public enum WireProtocol
{
    Auto = -1,
    /// <summary>OpenAI chat completions, also spoken by most compatible gateways.</summary>
    OpenAiChat = 0,
    OpenAiResponses,
    Anthropic,
    AzureOpenAi,
    Gemini,
    Bedrock,
    Vertex,
}

public enum BodyEncoding
{
    None,
    Gzip,
}

/// <summary>Null members mean "no limit". <see cref="Chunk"/> is the longest silence allowed between streamed reads.</summary>
public sealed record TransportLimits(TimeSpan? Total = null, TimeSpan? Header = null, TimeSpan? Chunk = null);

/// <summary>
/// Everything needed to talk to one provider endpoint, already resolved from settings and the environment.
/// Providers does not read settings itself, so the composition layer builds this.
/// </summary>
public sealed record ProviderConnection
{
    public required string Id { get; init; }

    public WireProtocol Wire { get; init; } = WireProtocol.Auto;

    public string? Canonical { get; init; }

    public string? CatalogModelId { get; init; }

    public CodeAssist.Models.Catalog.CatalogModel? ModelMetadata { get; init; }

    /// <summary>Null selects the adapter's default endpoint (or one derived from <c>resourceName</c> on Azure).</summary>
    public Uri? Endpoint { get; init; }

    /// <summary>Null means no key: loopback servers, or Microsoft Entra sign-in on Azure.</summary>
    public string? ApiKey { get; init; }

    public int MaxOutputTokens { get; init; } = 8192;
    public bool HostedExecutionEnabled { get; init; }
    public IReadOnlyList<HostedConnectorProfile> HostedConnectors { get; init; } = [];

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>Top-level request body fields as raw JSON text; objects are merged into the request's own object.</summary>
    public IReadOnlyDictionary<string, string> Body { get; init; } = new Dictionary<string, string>();

    /// <summary>Adapter-specific options such as <c>apiVersion</c> or <c>resourceName</c> (Azure).</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();

    public TransportLimits Limits { get; init; } = new();

    public BodyEncoding Encoding { get; init; } = BodyEncoding.None;

    public string? Proxy { get; init; }

    public int MaxConcurrentRequests { get; init; } = 4;
}
