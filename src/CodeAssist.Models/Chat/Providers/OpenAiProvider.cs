using Microsoft.Extensions.AI;
using OpenAI;

namespace CodeAssist.Models.Chat.Providers;

/// <summary>First-party OpenAI. Uses the Responses API unless the connection asks for chat completions.</summary>
public sealed class OpenAiProvider() : OpenAiCompatibleProvider("openai", "OpenAI", "https://api.openai.com/v1/", ["OPENAI_API_KEY"])
{
    public override WireProtocol Wire => WireProtocol.OpenAiResponses;

    public override IChatClient CreateChatClient(ProviderConnection connection, string model, HttpClient transport)
    {
        if (connection.Wire != WireProtocol.OpenAiResponses) return base.CreateChatClient(connection, model, transport);

        // The Responses client is marked experimental by the OpenAI SDK.
#pragma warning disable OPENAI001
        var responses = CreateOpenAiClient(connection, transport).GetResponsesClient().AsIChatClient(model);
#pragma warning restore OPENAI001
        return Finish(responses, connection, transport);
    }
}
