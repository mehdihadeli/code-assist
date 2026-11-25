using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CodeAssist.Configuration;

public enum ProviderProtocol
{
    [JsonStringEnumMemberName("openai")] OpenAi,
    [JsonStringEnumMemberName("anthropic")] Anthropic,
    [JsonStringEnumMemberName("azure")] Azure,
    [JsonStringEnumMemberName("auto")] Auto,
    [JsonStringEnumMemberName("gemini")] Gemini,
    [JsonStringEnumMemberName("vertex")] Vertex,
    [JsonStringEnumMemberName("bedrock")] Bedrock,
}

public enum ProviderApi
{
    /// <summary>Responses for first-party OpenAI and Azure endpoints, chat completions elsewhere.</summary>
    [JsonStringEnumMemberName("auto")] Auto,
    [JsonStringEnumMemberName("chat")] Chat,
    [JsonStringEnumMemberName("responses")] Responses,
}

public enum RequestEncoding
{
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("gzip")] Gzip,
    [JsonStringEnumMemberName("zstd")] Zstd,
}

public enum ProviderTransport
{
    [JsonStringEnumMemberName("http")] Http,
    /// <summary>One socket per session on routes that support it; falls back to HTTP otherwise.</summary>
    [JsonStringEnumMemberName("websocket")] WebSocket,
}

public enum CompactionStrategy
{
    [JsonStringEnumMemberName("summary")] Summary,
    /// <summary>Use the provider's own compaction endpoint when the route has one.</summary>
    [JsonStringEnumMemberName("native")] Native,
}

public enum ModelType
{
    [JsonStringEnumMemberName("chat")] Chat,
    [JsonStringEnumMemberName("image")] Image,
    [JsonStringEnumMemberName("embedding")] Embedding,
}

public enum CacheFormat
{
    [JsonStringEnumMemberName("auto")] Auto,
    /// <summary>cache_control markers on tools, system prompt and the latest message.</summary>
    [JsonStringEnumMemberName("anthropic")] Anthropic,
    /// <summary>Same markers, skipped on tool definitions.</summary>
    [JsonStringEnumMemberName("alibaba")] Alibaba,
}

public enum Modality
{
    [JsonStringEnumMemberName("text")] Text,
    [JsonStringEnumMemberName("image")] Image,
    [JsonStringEnumMemberName("pdf")] Pdf,
    [JsonStringEnumMemberName("audio")] Audio,
}

/// <summary>A model provider endpoint. Providers are user/managed-tier only: a repository can never introduce one.</summary>
public sealed class ProviderSettings
{
    [Description("Wire protocol spoken by the endpoint. Auto uses the provider adapter or models.dev catalog.")]
    public ProviderProtocol Protocol { get; set; } = ProviderProtocol.Auto;

    [Description("Request API for openai and azure protocols: chat completions or Responses. Auto picks Responses for first-party endpoints.")]
    public ProviderApi Api { get; set; } = ProviderApi.Auto;

    [Description("Base URL, or empty to use the provider adapter or catalog. HTTPS is required except on loopback; credentials in the URL are rejected.")]
    public string Endpoint { get; set; } = "";

    [Secret, Description("API key supplied by Providers__<id>__ApiKey, or an exact {env:VARIABLE_NAME} reference in trusted user/managed settings. Literal keys are never stored in files.")]
    public string? ApiKey { get; set; }

    [Description("Name of a credential in the vault (resolved when the vault adapter exists).")]
    public string? Credential { get; set; }

    [Description("Display name shown in pickers.")]
    public string? DisplayName { get; set; }

    [Description("Id of a catalog provider whose models, limits and prices this provider inherits (for gateways and proxies).")]
    public string? Canonical { get; set; }

    [Description("Probe the endpoint for installed models (Ollama, LM Studio, vLLM). Needs no catalog entry.")]
    public bool Discover { get; set; }

    [Description("Disabled providers stay listed but are never used.")]
    public bool Enabled { get; set; } = true;
    public HostedConnectorsSettings RemoteTools { get; set; } = new();

    [Range(1, 1_000_000), Description("Default cap on generated tokens per request.")]
    public int MaxOutputTokens { get; set; } = 8192;

    [Range(1, 3600), Description("Whole-request timeout in seconds.")]
    public int TimeoutSeconds { get; set; } = 180;

    [Range(1, 3600), Description("Seconds to wait for response headers.")]
    public int HeaderTimeoutSeconds { get; set; } = 300;

    [Range(1, 7200), Description("Seconds allowed between streamed chunks.")]
    public int ChunkTimeoutSeconds { get; set; } = 900;

    [Description("Compression for request bodies. Enable only when the endpoint decodes it.")]
    public RequestEncoding Encoding { get; set; } = RequestEncoding.None;

    [Description("http, or websocket to reuse one connection per session where supported.")]
    public ProviderTransport Transport { get; set; } = ProviderTransport.Http;

    [Description("Context compaction: local summary or the provider's native endpoint.")]
    public CompactionStrategy Compaction { get; set; } = CompactionStrategy.Summary;

    [Description("Request and response quirks shared by every model of this provider.")]
    public CompatibilitySettings Compatibility { get; set; } = new();

    [Description("JSON fields merged into every request body, as raw JSON text per top-level field.")]
    public SettingsMap<string> Body { get; set; } = new();

    [Range(1, 256), Description("Maximum simultaneous requests to this provider.")]
    public int MaxConcurrentRequests { get; set; } = 4;

    [Range(1, 1_000_000), Description("Optional client-side rate limit.")]
    public int? RequestsPerMinute { get; set; }

    [Description("Extra request headers (never put secrets here).")]
    public SettingsMap<string> Headers { get; set; } = new();

    [Description("HTTP proxy URL for this provider.")]
    public string? Proxy { get; set; }

    [Description("Adapter-specific options, for example apiVersion or deployment for Azure.")]
    public SettingsMap<string> Options { get; set; } = new();

    [Description("Models offered by this provider; entries add to or override the shipped catalog.")]
    public SettingsMap<ModelSettings> Models { get; set; } = new();

    [Description("Named parameter sets applied to every model of this provider (model refs use provider/model@variant).")]
    public SettingsMap<VariantSettings> Variants { get; set; } = new();
}

public sealed class ModelSettings
{
    [Description("Provider-side model id when it differs from the key.")]
    public string? ModelId { get; set; }

    [Description("Display name.")]
    public string? Name { get; set; }

    public bool Enabled { get; set; } = true;

    [Description("Deprecation note shown when the model is selected.")]
    public string? Deprecated { get; set; }

    [Range(1, 100_000_000)]
    public int? ContextWindow { get; set; }

    [Range(1, 10_000_000)]
    public int? MaxOutputTokens { get; set; }

    public List<Modality> Input { get; set; } = [Modality.Text];
    public List<Modality> Output { get; set; } = [Modality.Text];

    [Description("The model supports tool calls.")]
    public bool Tools { get; set; } = true;

    [Description("The model produces reasoning content and accepts an effort variant.")]
    public bool Reasoning { get; set; }

    [Description("Related models grouped together, for example 'claude-sonnet'.")]
    public string? Family { get; set; }

    [Description("What the model produces. Only chat models can run sessions.")]
    public ModelType Type { get; set; } = ModelType.Chat;

    [RegularExpression(@"^\d{4}-\d{2}(-\d{2})?$"), Description("YYYY-MM or YYYY-MM-DD.")]
    public string? ReleaseDate { get; set; }

    [Range(1, 100_000_000), Description("Input-token limit when it differs from the context window.")]
    public int? InputTokenLimit { get; set; }

    [Description("The model accepts a temperature.")]
    public bool Temperature { get; set; } = true;

    [Description("Reasoning controls; they generate effort variants (plus 'none' when toggleable).")]
    public ReasoningSettings ReasoningControls { get; set; } = new();

    [Description("Quirks of this model's API; unset fields inherit the provider's.")]
    public CompatibilitySettings Compatibility { get; set; } = new();

    [Description("Extra request headers for this model.")]
    public SettingsMap<string> Headers { get; set; } = new();

    [Description("Adapter options for this model; applied after the provider's.")]
    public SettingsMap<string> Options { get; set; } = new();

    [Description("Raw JSON fields merged into this model's request bodies.")]
    public SettingsMap<string> Body { get; set; } = new();

    public ModelCostSettings Cost { get; set; } = new();
    public SettingsMap<VariantSettings> Variants { get; set; } = new();
}

public sealed class ReasoningSettings
{
    [Description("Adds a 'none' variant that turns reasoning off.")]
    public bool Toggle { get; set; }

    [Description("Effort levels offered as variants, for example low, medium, high.")]
    public List<string> Efforts { get; set; } = [];

    [Range(0, 10_000_000), Description("Minimum thinking tokens; ignored when efforts are set.")]
    public int? BudgetMin { get; set; }

    [Range(0, 10_000_000), Description("Maximum thinking tokens; ignored when efforts are set.")]
    public int? BudgetMax { get; set; }
}

/// <summary>API quirks. Null means the adapter's own default or detection; a field only matters on the API that reads it.</summary>
public sealed class CompatibilitySettings
{
    [Description("Chat completions: assistant-message field that carries reasoning, for example reasoning_content.")]
    public string? ReasoningField { get; set; }

    [Description("Chat completions: send the reasoning field on every prior assistant message.")]
    public bool? RequireReasoning { get; set; }

    [RegularExpression("^(max_tokens|max_completion_tokens)$"), Description("Chat completions: body field for the output limit.")]
    public string? MaxTokensField { get; set; }

    [Description("Chat completions: treat a stream with no finish_reason as incomplete. False accepts it.")]
    public bool? RequireFinishReason { get; set; }

    [Description("Chat completions: insert an assistant message between a tool result and the next user message.")]
    public bool? RequireAssistantAfterTool { get; set; }

    [Description("Whether the endpoint accepts prompt_cache_key.")]
    public bool? SupportsPromptCacheKey { get; set; }

    [Description("Chat completions: request usage in the stream (stream_options.include_usage).")]
    public bool? StreamUsage { get; set; }

    [Description("Prompt-cache marker syntax, for Claude models behind an OpenAI-compatible proxy.")]
    public CacheFormat? CacheFormat { get; set; }

    [Description("Messages API: ask the server to drop thinking blocks whose context changed.")]
    public bool? SupportsThinkingBlockBinding { get; set; }

    [Description("Send effort changes as per-message updates so the prompt cache survives.")]
    public bool? SupportsEffortUpdates { get; set; }
}

public sealed class ModelCostSettings
{
    [Range(0, 1_000_000)] public decimal? InputPerMillion { get; set; }
    [Range(0, 1_000_000)] public decimal? OutputPerMillion { get; set; }
    [Range(0, 1_000_000)] public decimal? CacheReadPerMillion { get; set; }
    [Range(0, 1_000_000)] public decimal? CacheWritePerMillion { get; set; }

    [Description("Higher prices that apply once the context exceeds a size.")]
    public List<ModelCostTier> Tiers { get; set; } = [];
}

public sealed class ModelCostTier
{
    [Range(1, 100_000_000)] public int AboveTokens { get; set; }
    [Range(0, 1_000_000)] public decimal? InputPerMillion { get; set; }
    [Range(0, 1_000_000)] public decimal? OutputPerMillion { get; set; }
    [Range(0, 1_000_000)] public decimal? CacheReadPerMillion { get; set; }
    [Range(0, 1_000_000)] public decimal? CacheWritePerMillion { get; set; }
}

/// <summary>A request-time overlay: it never changes the stored catalog.</summary>
public sealed class VariantSettings
{
    [Description("Reasoning effort hint: minimal, low, medium, high or a provider-specific word.")]
    public string? Effort { get; set; }

    [Range(0, 2)] public double? Temperature { get; set; }
    [Range(0, 1)] public double? TopP { get; set; }

    [Range(0, 10_000_000), Description("Thinking-token budget for models that take one instead of an effort.")]
    public int? ThinkingBudget { get; set; }

    public SettingsMap<string> Headers { get; set; } = new();
    public SettingsMap<string> Options { get; set; } = new();
    public SettingsMap<string> Body { get; set; } = new();
}
