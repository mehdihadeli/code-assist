using Amazon;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using Microsoft.Extensions.AI;

namespace CodeAssist.Models.Chat.Providers;

/// <summary>
/// Amazon Bedrock (Converse API) through AWS's official Microsoft.Extensions.AI adapter. A key is a Bedrock API key
/// (bearer token); without one the standard AWS credential chain applies (environment, profile, SSO, instance role).
/// Options: <c>region</c> (else AWS_REGION) and <c>profile</c>.
/// </summary>
public sealed class AmazonBedrockProvider : ProviderAdapter
{
    public const string RegionOption = "region";
    public const string ProfileOption = "profile";

    public override string Id => "amazon-bedrock";

    public override string DisplayName => "Amazon Bedrock";

    public override WireProtocol Wire => WireProtocol.Bedrock;

    public override IReadOnlyList<string> KeyVariables => ["AWS_BEARER_TOKEN_BEDROCK"];

    public override bool KeyOptional => true;

    public override IChatClient CreateChatClient(ProviderConnection connection, string model, HttpClient transport)
    {
        if (string.IsNullOrWhiteSpace(connection.ApiKey) &&
            (connection.Body.Count > 0 || connection.Headers.Count > 0 || connection.Encoding != BodyEncoding.None))
            throw new NotSupportedException("HTTP request overlays and compression cannot be applied after AWS SigV4 signing. Use SDK chat options or a Bedrock API key.");

        var config = new AmazonBedrockRuntimeConfig
        {
            MaxErrorRetry = 0,
            HttpClientFactory = new FixedHttpClientFactory(transport),
        };
        if (connection.Options.TryGetValue(RegionOption, out var region)) config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        if (connection.Options.TryGetValue(ProfileOption, out var profile)) config.Profile = new Profile(profile);
        if (connection.Endpoint is not null) config.ServiceURL = ResolveEndpoint(connection).AbsoluteUri;
        if (!string.IsNullOrWhiteSpace(connection.ApiKey)) config.AWSTokenProvider = new ServiceBearerStaticTokenProvider(connection.ApiKey, null);

        var runtime = new AmazonBedrockRuntimeClient(config);
        return Finish(new OwningChatClient(runtime.AsIChatClient(model), runtime), connection, transport);
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => client;

        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;
    }

    private sealed class OwningChatClient(IChatClient inner, IDisposable owned) : DelegatingChatClient(inner)
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing) owned.Dispose();
            base.Dispose(disposing);
        }
    }
}
