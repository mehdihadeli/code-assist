using System.ClientModel.Primitives;
using System.Text.RegularExpressions;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;

namespace CodeAssist.Models.Chat.Providers;

/// <summary>
/// Azure OpenAI. The model name is the deployment name. Without a key it signs in with the developer's Microsoft Entra
/// identity. Options: <c>resourceName</c> (builds the endpoint) and <c>apiVersion</c> such as 2024-10-21.
/// </summary>
public sealed partial class AzureProvider : ProviderAdapter
{
    public const string ResourceNameOption = "resourceName";
    public const string ApiVersionOption = "apiVersion";

    public override string Id => "azure";

    public override string DisplayName => "Azure OpenAI";

    public override WireProtocol Wire => WireProtocol.AzureOpenAi;

    public override IReadOnlyList<string> KeyVariables => ["AZURE_API_KEY", "AZURE_OPENAI_API_KEY"];

    public override IChatClient CreateChatClient(ProviderConnection connection, string model, HttpClient transport)
    {
        var endpoint = ResolveEndpoint(connection);
        var options = new AzureOpenAIClientOptions(ParseServiceVersion(connection))
        {
            Transport = new HttpClientPipelineTransport(transport),
            RetryPolicy = new ClientRetryPolicy(0),
        };

        var client = string.IsNullOrWhiteSpace(connection.ApiKey)
            ? new AzureOpenAIClient(endpoint, new DefaultAzureCredential(), options)
            : new AzureOpenAIClient(endpoint, new AzureKeyCredential(connection.ApiKey), options);
        return Finish(client.GetChatClient(model).AsIChatClient(), connection, transport);
    }

    protected override Uri ResolveEndpoint(ProviderConnection connection)
    {
        if (connection.Endpoint is null && connection.Options.TryGetValue(ResourceNameOption, out var name))
        {
            if (!ResourceName().IsMatch(name)) throw new ArgumentException($"Azure resource name '{name}' is not valid.");
            return new Uri($"https://{name}.openai.azure.com/");
        }
        return base.ResolveEndpoint(connection);
    }

    private static AzureOpenAIClientOptions.ServiceVersion ParseServiceVersion(ProviderConnection connection)
    {
        if (!connection.Options.TryGetValue(ApiVersionOption, out var version)) return default;

        var name = "V" + version.Replace("-preview", "_Preview", StringComparison.OrdinalIgnoreCase).Replace('-', '_');
        return Enum.TryParse<AzureOpenAIClientOptions.ServiceVersion>(name, ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentException($"Azure apiVersion '{version}' is not supported by this build. Supported: {string.Join(", ", Enum.GetNames<AzureOpenAIClientOptions.ServiceVersion>())}.");
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]{0,62}$")]
    private static partial Regex ResourceName();
}
