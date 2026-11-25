using Microsoft.Extensions.AI;

namespace CodeAssist.Models.Chat.Providers;

/// <summary>Gemini API through Google's official GenAI SDK.</summary>
public sealed class GoogleProvider : ProviderAdapter
{
    public override string Id => "google";

    public override string DisplayName => "Google Gemini";

    public override WireProtocol Wire => WireProtocol.Gemini;

    public override string? DefaultEndpoint => "https://generativelanguage.googleapis.com/";

    public override IReadOnlyList<string> KeyVariables => ["GEMINI_API_KEY", "GOOGLE_GENERATIVE_AI_API_KEY", "GOOGLE_API_KEY"];

    public override IChatClient CreateChatClient(ProviderConnection connection, string model, HttpClient transport)
    {
        var endpoint = WithTrailingSlash(ResolveEndpoint(connection));
        var key = KeyOrPlaceholder(connection, endpoint);
        return Finish(GeminiClients.Create(false, key, null, null, endpoint, model, transport), connection, transport);
    }
}
