using Microsoft.Extensions.AI;

namespace CodeAssist.Models.Chat.Providers;

/// <summary>
/// Gemini on Google Cloud Vertex AI through the official GenAI SDK. With a key it uses express mode; without one it
/// signs in with Application Default Credentials. Options: <c>project</c> and <c>location</c>, otherwise the SDK reads
/// GOOGLE_CLOUD_PROJECT and GOOGLE_CLOUD_LOCATION.
/// </summary>
public sealed class GoogleVertexProvider : ProviderAdapter
{
    public const string ProjectOption = "project";
    public const string LocationOption = "location";

    public override string Id => "google-vertex";

    public override string DisplayName => "Google Vertex AI";

    public override WireProtocol Wire => WireProtocol.Vertex;

    public override IReadOnlyList<string> KeyVariables => ["GOOGLE_VERTEX_API_KEY"];

    public override bool KeyOptional => true;

    public override IChatClient CreateChatClient(ProviderConnection connection, string model, HttpClient transport)
    {
        var endpoint = connection.Endpoint is null ? null : WithTrailingSlash(ResolveEndpoint(connection));
        var key = string.IsNullOrWhiteSpace(connection.ApiKey) ? null : connection.ApiKey;
        var project = key is null ? connection.Options.GetValueOrDefault(ProjectOption) : null;
        var location = key is null ? connection.Options.GetValueOrDefault(LocationOption) : null;
        return Finish(GeminiClients.Create(true, key, project, location, endpoint, model, transport), connection, transport);
    }
}
