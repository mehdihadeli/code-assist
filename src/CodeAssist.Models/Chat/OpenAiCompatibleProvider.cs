using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;

namespace CodeAssist.Models.Chat;

/// <summary>Any endpoint that implements OpenAI chat completions. Named providers derive from it with their own endpoint and keys.</summary>
public class OpenAiCompatibleProvider(
    string id, string displayName, string? defaultEndpoint, IReadOnlyList<string> keyVariables, bool keyOptional = false) : ProviderAdapter
{
    public override string Id => id;

    public override string DisplayName => displayName;

    public override WireProtocol Wire => WireProtocol.OpenAiChat;

    public override string? DefaultEndpoint => defaultEndpoint;

    public override IReadOnlyList<string> KeyVariables => keyVariables;

    public override bool KeyOptional => keyOptional;

    public override IChatClient CreateChatClient(ProviderConnection connection, string model, HttpClient transport)
    {
        var chat = CreateOpenAiClient(connection, transport).GetChatClient(model).AsIChatClient();
        return Finish(chat, connection, transport);
    }

    protected OpenAIClient CreateOpenAiClient(ProviderConnection connection, HttpClient transport)
    {
        var endpoint = ResolveEndpoint(connection);
        return new OpenAIClient(new ApiKeyCredential(KeyOrPlaceholder(connection, endpoint)), new OpenAIClientOptions
        {
            Endpoint = WithTrailingSlash(endpoint),
            Transport = new HttpClientPipelineTransport(transport),
            RetryPolicy = new ClientRetryPolicy(0),
        });
    }
}
