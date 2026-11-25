using CodeAssist.Models.Catalog;
using Microsoft.Extensions.AI;

namespace CodeAssist.Models.Chat;

/// <summary>
/// Knows how one provider (or family of providers) is reached: its default endpoint, key variables and the client
/// that speaks its wire protocol. Adapters hold no state, so one instance serves every connection.
/// </summary>
public abstract class ProviderAdapter
{
    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    public abstract WireProtocol Wire { get; }

    public virtual string? DefaultEndpoint => null;

    /// <summary>Conventional environment variables that may hold the API key, in order.</summary>
    public virtual IReadOnlyList<string> KeyVariables => [];

    /// <summary>The provider works without a key (local runtimes).</summary>
    public virtual bool KeyOptional => false;

    public abstract IChatClient CreateChatClient(ProviderConnection connection, string model, HttpClient transport);

    /// <summary>Picks the connection's endpoint or the default, and rejects anything that could leak the key.</summary>
    protected virtual Uri ResolveEndpoint(ProviderConnection connection)
    {
        var endpoint = connection.Endpoint ?? (DefaultEndpoint is { } fallback ? new Uri(fallback) : null)
            ?? throw new ArgumentException($"Provider '{connection.Id}' needs an endpoint.");

        if (!IsAllowed(endpoint))
            throw new ArgumentException("Provider endpoint must be HTTPS (HTTP only on loopback), without credentials, query or fragment.");
        return endpoint;
    }

    /// <summary>The key to send, a placeholder for loopback servers, or an error when a remote endpoint has none.</summary>
    protected static string KeyOrPlaceholder(ProviderConnection connection, Uri endpoint) =>
        !string.IsNullOrWhiteSpace(connection.ApiKey)
            ? connection.ApiKey
            : endpoint.IsLoopback
                ? "local"
                : throw new InvalidOperationException($"Provider '{connection.Id}' has no API key.");

    protected static Uri WithTrailingSlash(Uri endpoint) => new(endpoint.AbsoluteUri.TrimEnd('/') + "/");

    protected static IChatClient Finish(IChatClient client, ProviderConnection connection, HttpClient transport) =>
        new ProviderChatClient(client, connection.MaxOutputTokens, transport);

    private static bool IsAllowed(Uri endpoint) =>
        (endpoint.Scheme == Uri.UriSchemeHttps || (endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback)) &&
        endpoint.UserInfo.Length == 0 && endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0;
}

/// <summary>Applies the connection's default output cap and owns the HTTP client for the lifetime of the chat client.</summary>
internal sealed class ProviderChatClient(IChatClient inner, int maxOutputTokens, HttpClient transport) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, WithDefaults(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, WithDefaults(options), cancellationToken);

    private ChatOptions WithDefaults(ChatOptions? options)
    {
        var result = options?.Clone() ?? new ChatOptions();
        result.MaxOutputTokens ??= maxOutputTokens;
        return result;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) transport.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class CatalogChatClient(IChatClient inner, string defaultModel, int defaultMaxOutputTokens, Func<string, CatalogModel?> lookup) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var history = messages.ToArray();
        return base.GetResponseAsync(history, Prepare(history, options), cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var history = messages.ToArray();
        return base.GetStreamingResponseAsync(history, Prepare(history, options), cancellationToken);
    }

    private ChatOptions? Prepare(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var model = lookup(options?.ModelId ?? defaultModel);
        if (model is null) return options;
        if (model.Output.Count > 0 && !model.Output.Contains("text", StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException($"Model '{model.ProviderId}/{model.ModelId}' does not produce text responses.");
        if (model.ToolCalls == false && options?.Tools is { Count: > 0 })
            throw new NotSupportedException($"Model '{model.ProviderId}/{model.ModelId}' does not support tool calls.");
        if (model.StructuredOutput == false && options?.ResponseFormat is ChatResponseFormatJson { Schema: not null })
            throw new NotSupportedException($"Model '{model.ProviderId}/{model.ModelId}' does not support structured output.");

        if (model.Input.Count > 0)
        {
            foreach (var content in messages.SelectMany(message => message.Contents))
            {
                var mediaType = content switch
                {
                    DataContent data => data.MediaType,
                    UriContent uri => uri.MediaType,
                    _ => null,
                };
                var modality = mediaType?.Split(';', 2)[0].Trim().ToLowerInvariant() switch
                {
                    "application/pdf" => "pdf",
                    { } type when type.StartsWith("image/", StringComparison.Ordinal) => "image",
                    { } type when type.StartsWith("audio/", StringComparison.Ordinal) => "audio",
                    { } type when type.StartsWith("video/", StringComparison.Ordinal) => "video",
                    _ => null,
                };
                if (modality is not null && !model.Input.Contains(modality, StringComparer.OrdinalIgnoreCase))
                    throw new NotSupportedException($"Model '{model.ProviderId}/{model.ModelId}' does not support {modality} input.");
            }
        }

        var result = options?.Clone() ?? new ChatOptions();
        if (model.Temperature == false) result.Temperature = null;
        if (model.MaxOutputTokens is > 0)
            result.MaxOutputTokens = Math.Min(result.MaxOutputTokens ?? defaultMaxOutputTokens, model.MaxOutputTokens.Value);
        return result;
    }
}
