using Anthropic;
using Microsoft.Extensions.AI;

namespace CodeAssist.Models.Chat.Providers;

/// <summary>Anthropic Messages API, also used for Anthropic-compatible gateways that share the wire format.</summary>
public sealed class AnthropicProvider : ProviderAdapter
{
    public override string Id => "anthropic";

    public override string DisplayName => "Anthropic";

    public override WireProtocol Wire => WireProtocol.Anthropic;

    public override string DefaultEndpoint => "https://api.anthropic.com/";

    public override IReadOnlyList<string> KeyVariables => ["ANTHROPIC_API_KEY"];

    public override IChatClient CreateChatClient(ProviderConnection connection, string model, HttpClient transport)
    {
        var endpoint = ResolveEndpoint(connection);
        var baseUrl = endpoint.AbsoluteUri.TrimEnd('/');
        if (baseUrl.EndsWith("/v1", StringComparison.Ordinal)) baseUrl = baseUrl[..^3];

        var key = KeyOrPlaceholder(connection, endpoint);
        // The SDK sends the key as a bearer token; Anthropic's own API expects x-api-key.
        transport.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", key);

        var client = new AnthropicClient
        {
            ApiKey = key,
            BaseUrl = baseUrl,
            HttpClient = transport,
            MaxRetries = 0,
        };
        return Finish(connection.HostedExecutionEnabled
            ? new HostedConnectorChatClient(client.Beta.AsIChatClient(defaultModelId: model, defaultMaxOutputTokens: connection.MaxOutputTokens))
            : client.AsIChatClient(defaultModelId: model, defaultMaxOutputTokens: connection.MaxOutputTokens), connection, transport);
    }
}
