using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;

namespace CodeAssist.Models.Chat;

/// <summary>Builds Google GenAI SDK clients that send every request through the connection's HTTP stack.</summary>
internal static class GeminiClients
{
    public static IChatClient Create(
        bool vertex, string? apiKey, string? project, string? location, Uri? endpoint, string model, HttpClient transport)
    {
        var http = new HttpOptions
        {
            BaseUrl = endpoint?.AbsoluteUri,
            RetryOptions = new HttpRetryOptions { Attempts = 1 },
        };
        var client = new Client(
            vertexAI: vertex, enterprise: vertex, apiKey: apiKey, project: project, location: location,
            httpOptions: http, clientOptions: new ClientOptions { HttpClientFactory = () => transport });
        return client.AsIChatClient(model);
    }
}
