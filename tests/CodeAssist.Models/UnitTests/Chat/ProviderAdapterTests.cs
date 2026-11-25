using System.IO.Compression;
using System.Net;
using System.Text;
using CodeAssist.Models.Catalog;
using CodeAssist.Models.Chat;
using Microsoft.Extensions.AI;
using Shouldly;
using System.Text.Json.Nodes;

namespace CodeAssist.Models.UnitTests.Chat;

public sealed class ProviderAdapterTests
{
    private const string ChatCompletion = """
        {"id":"c1","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
        """;

    private const string AnthropicMessage = """
        {"id":"msg_1","type":"message","role":"assistant","model":"m","content":[{"type":"text","text":"OK"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}
        """;

    private const string Responses = """
        {"id":"r1","object":"response","created_at":1,"status":"completed","model":"m","output":[{"type":"message","id":"m1","status":"completed","role":"assistant","content":[{"type":"output_text","text":"OK","annotations":[]}]}],"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}
        """;

    private const string GeminiResponse = """
        {"candidates":[{"content":{"role":"model","parts":[{"text":"OK"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":1,"candidatesTokenCount":1,"totalTokenCount":2}}
        """;

    private const string BedrockResponse = """
        {"output":{"message":{"role":"assistant","content":[{"text":"OK"}]}},"stopReason":"end_turn","usage":{"inputTokens":1,"outputTokens":1,"totalTokens":2},"metrics":{"latencyMs":1}}
        """;

    private static ProviderConnection Connection(string id, WireProtocol wire = WireProtocol.Auto, string? key = "secret") =>
        new() { Id = id, Wire = wire, ApiKey = key };

    private static async Task<(Capture Request, ChatResponse Response)> Call(
        ProviderConnection connection, string model, string responseJson, ProviderRegistry? registry = null)
    {
        var capture = new Capture(responseJson);
        using var client = (registry ?? ProviderRegistry.CreateDefault()).CreateChatClient(connection, model, capture);
        var response = await client.GetResponseAsync("hi");
        return (capture, response);
    }

    [Fact]
    public async Task NamedOpenAiCompatibleProviderUsesItsEndpointAndBearerKey()
    {
        var (request, response) = await Call(Connection("groq"), "llama", ChatCompletion);

        request.Uri.ToString().ShouldBe("https://api.groq.com/openai/v1/chat/completions");
        request.Headers["Authorization"].ShouldBe("Bearer secret");
        request.Body!["model"]!.GetValue<string>().ShouldBe("llama");
        (request.Body["max_completion_tokens"] ?? request.Body["max_tokens"])!.GetValue<int>().ShouldBe(8192);
        response.Text.ShouldBe("OK");
    }

    [Fact]
    public async Task OpenAiUsesResponsesApiByDefault()
    {
        var (request, response) = await Call(Connection("openai", WireProtocol.OpenAiResponses), "gpt", Responses);

        request.Uri.ToString().ShouldBe("https://api.openai.com/v1/responses");
        response.Text.ShouldBe("OK");
    }

    [Fact]
    public async Task OpenAiCanUseChatCompletions()
    {
        var (request, _) = await Call(Connection("openai", WireProtocol.OpenAiChat), "gpt", ChatCompletion);

        request.Uri.AbsolutePath.ShouldBe("/v1/chat/completions");
    }

    [Fact]
    public async Task AnthropicSendsKeyHeaderAndMaxTokens()
    {
        var (request, response) = await Call(Connection("anthropic", WireProtocol.Anthropic), "claude", AnthropicMessage);

        request.Uri.ToString().ShouldBe("https://api.anthropic.com/v1/messages");
        request.Headers.ContainsKey("x-api-key").ShouldBeTrue(string.Join(", ", request.Headers.Select(h => h.Key + "=" + h.Value)));
        request.Headers["x-api-key"].ShouldBe("secret");
        request.Body!["max_tokens"]!.GetValue<int>().ShouldBe(8192);
        response.Text.ShouldBe("OK");
    }

    [Fact]
    public async Task AnthropicCompatibleEndpointKeepsItsPathWithoutDoubleVersion()
    {
        var connection = Connection("minimax", WireProtocol.Anthropic) with { Endpoint = new Uri("https://gateway.example/anthropic/v1") };

        var (request, _) = await Call(connection, "m", AnthropicMessage);

        request.Uri.ToString().ShouldBe("https://gateway.example/anthropic/v1/messages");
    }

    [Fact]
    public async Task AzureBuildsEndpointFromResourceNameAndUsesDeploymentPath()
    {
        var connection = Connection("azure", WireProtocol.AzureOpenAi) with
        {
            Options = new Dictionary<string, string> { ["resourceName"] = "my-res", ["apiVersion"] = "2024-10-21" },
        };

        var (request, _) = await Call(connection, "dep", ChatCompletion);

        request.Uri.Host.ShouldBe("my-res.openai.azure.com");
        request.Uri.AbsolutePath.ShouldBe("/openai/deployments/dep/chat/completions");
        request.Uri.Query.ShouldContain("api-version=2024-10-21");
        request.Headers["api-key"].ShouldBe("secret");
    }

    [Theory]
    [InlineData("bad.name")]
    [InlineData("a/b")]
    [InlineData("-lead")]
    public void AzureRejectsInvalidResourceNames(string name)
    {
        var connection = Connection("azure", WireProtocol.AzureOpenAi) with
        {
            Options = new Dictionary<string, string> { ["resourceName"] = name },
        };

        Should.Throw<ArgumentException>(() => ProviderRegistry.CreateDefault().CreateChatClient(connection, "dep", new Capture(ChatCompletion)));
    }

    [Fact]
    public void AzureRejectsUnknownApiVersion()
    {
        var connection = Connection("azure", WireProtocol.AzureOpenAi) with
        {
            Options = new Dictionary<string, string> { ["resourceName"] = "r", ["apiVersion"] = "1999-01-01" },
        };

        Should.Throw<ArgumentException>(() => ProviderRegistry.CreateDefault().CreateChatClient(connection, "dep", new Capture(ChatCompletion)));
    }

    [Fact]
    public void RemoteProviderWithoutKeyIsRejected()
    {
        Should.Throw<InvalidOperationException>(() =>
            ProviderRegistry.CreateDefault().CreateChatClient(Connection("groq", key: null), "m", new Capture(ChatCompletion)));
    }

    [Fact]
    public async Task LoopbackProviderWorksWithoutKey()
    {
        var (request, _) = await Call(Connection("ollama", key: null), "llama3", ChatCompletion);

        request.Uri.ToString().ShouldBe("http://localhost:11434/v1/chat/completions");
    }

    [Theory]
    [InlineData("http://remote.example/v1")]
    [InlineData("https://user:pw@remote.example/v1")]
    [InlineData("https://remote.example/v1?x=1")]
    public void UnsafeEndpointsAreRejected(string endpoint)
    {
        var connection = Connection("groq") with { Endpoint = new Uri(endpoint) };

        Should.Throw<ArgumentException>(() => ProviderRegistry.CreateDefault().CreateChatClient(connection, "m", new Capture(ChatCompletion)));
    }

    [Fact]
    public async Task HeadersAndBodyFieldsAreOverlaidAndOverrideAdapterValues()
    {
        var connection = Connection("groq") with
        {
            Headers = new Dictionary<string, string> { ["X-Team"] = "core", ["Authorization"] = "Bearer override" },
            Body = new Dictionary<string, string> { ["reasoning_effort"] = "\"high\"", ["metadata"] = "{\"a\":1}" },
        };

        var (request, _) = await Call(connection, "m", ChatCompletion);

        request.Headers["X-Team"].ShouldBe("core");
        request.Headers["Authorization"].ShouldBe("Bearer override");
        request.Body!["reasoning_effort"]!.GetValue<string>().ShouldBe("high");
        request.Body["metadata"]!["a"]!.GetValue<int>().ShouldBe(1);
        request.Body["model"]!.GetValue<string>().ShouldBe("m");
    }

    [Fact]
    public async Task GzipEncodingCompressesTheBody()
    {
        var connection = Connection("groq") with { Encoding = BodyEncoding.Gzip };

        var (request, _) = await Call(connection, "m", ChatCompletion);

        request.Headers["Content-Encoding"].ShouldBe("gzip");
        request.Body!["model"]!.GetValue<string>().ShouldBe("m");
    }

    [Fact]
    public async Task HeaderTimeoutFailsAStalledProvider()
    {
        var connection = Connection("groq") with { Limits = new TransportLimits(Header: TimeSpan.FromMilliseconds(100)) };
        using var client = ProviderRegistry.CreateDefault().CreateChatClient(connection, "m", new Capture(ChatCompletion) { Stall = true });

        await Should.ThrowAsync<TimeoutException>(() => client.GetResponseAsync("hi"));
    }

    [Fact]
    public async Task ConcurrencyCapQueuesExtraRequests()
    {
        var connection = Connection("groq") with { MaxConcurrentRequests = 1 };
        var capture = new Capture(ChatCompletion) { Delay = TimeSpan.FromMilliseconds(150) };
        using var client = ProviderRegistry.CreateDefault().CreateChatClient(connection, "m", capture);

        var token = TestContext.Current.CancellationToken;
        await Task.WhenAll(
            client.GetResponseAsync("a", cancellationToken: token),
            client.GetResponseAsync("b", cancellationToken: token),
            client.GetResponseAsync("c", cancellationToken: token));

        capture.MaxActive.ShouldBe(1);
    }

    [Fact]
    public void RegistryReportsUnsupportedProvidersWithAReason()
    {
        var registry = ProviderRegistry.CreateDefault();

        Should.Throw<NotSupportedException>(() => registry.Resolve(Connection("github-copilot"))).Message.ShouldContain("Copilot");
    }

    [Fact]
    public async Task GoogleUsesNativeGeminiApiThroughTheConnectionTransport()
    {
        var capture = new Capture(GeminiResponse);
        using var client = ProviderRegistry.CreateDefault().CreateChatClient(Connection("google"), "gemini-x", capture);

        var response = await client.GetResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken);
        await client.GetResponseAsync("again", cancellationToken: TestContext.Current.CancellationToken);

        capture.Uri.AbsolutePath.ShouldEndWith("/models/gemini-x:generateContent");
        capture.Uri.Host.ShouldBe("generativelanguage.googleapis.com");
        capture.Headers["x-goog-api-key"].ShouldBe("secret");
        capture.Body!["generationConfig"]!["maxOutputTokens"]!.GetValue<int>().ShouldBe(8192);
        response.Text.ShouldBe("OK");
    }

    [Fact]
    public async Task BedrockUsesConverseWithApiKeyAndRegion()
    {
        var connection = Connection("amazon-bedrock") with { Options = new Dictionary<string, string> { ["region"] = "eu-west-1" } };

        var (request, response) = await Call(connection, "anthropic.claude-x", BedrockResponse);

        request.Uri.Host.ShouldBe("bedrock-runtime.eu-west-1.amazonaws.com");
        request.Uri.AbsolutePath.ShouldEndWith("/converse");
        request.Headers["Authorization"].ShouldBe("Bearer secret");
        request.Body!["inferenceConfig"]!["maxTokens"]!.GetValue<int>().ShouldBe(8192);
        response.Text.ShouldBe("OK");
    }

    [Fact]
    public void BedrockRejectsPostSigningRequestMutation()
    {
        var connection = Connection("amazon-bedrock", key: null) with
        {
            Body = new Dictionary<string, string> { ["metadata"] = "{}" },
        };

        Should.Throw<NotSupportedException>(() => ProviderRegistry.CreateDefault()
            .CreateChatClient(connection, "m", new Capture(BedrockResponse))).Message.ShouldContain("SigV4");
    }

    [Fact]
    public void NativeGeminiCanUseItsSdkEndpointWithoutACatalogUrl()
    {
        var entry = new CatalogProvider("custom-google", "Custom Google", null, ["GOOGLE_API_KEY"], "gemini", null);
        var registry = ProviderRegistry.CreateDefault(_ => entry);

        registry.Resolve(Connection("custom-google")).Connection.Endpoint!.Host.ShouldBe("generativelanguage.googleapis.com");
    }

    [Fact]
    public void CloudCatalogAliasesUseNativeCredentialRulesAndEndpointConstruction()
    {
        var entry = new CatalogProvider("custom-aws", "AWS", "https://bedrock-runtime.${AWS_REGION}.amazonaws.com",
            ["AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_REGION"], "bedrock", null);
        var registry = ProviderRegistry.CreateDefault(_ => entry);

        registry.Resolve(Connection("custom-aws")).Connection.Endpoint.ShouldBeNull();
        registry.KeyVariables("custom-aws").ShouldBe(["AWS_BEARER_TOKEN_BEDROCK"]);
    }

    [Fact]
    public void CanonicalMetadataDoesNotReplaceGatewayRouting()
    {
        var catalog = ModelCatalog.Parse(JsonNode.Parse("""
            {"canonical":{"protocol":"anthropic","models":{"m":{"limit":{"output":1024},"provider":{"api":"https://upstream.example"}}}}}
            """));
        var registry = ProviderRegistry.CreateDefault(catalog.FindProvider, catalog.Find);

        var resolved = registry.Resolve(Connection("gateway", WireProtocol.OpenAiChat) with
        {
            Canonical = "canonical", Endpoint = new Uri("https://gateway.example/v1"),
        }, "m").Connection;

        resolved.Endpoint!.Host.ShouldBe("gateway.example");
        resolved.Wire.ShouldBe(WireProtocol.OpenAiChat);
        resolved.MaxOutputTokens.ShouldBe(1024);
    }

    [Fact]
    public async Task VertexWithKeyUsesExpressMode()
    {
        var (request, response) = await Call(Connection("google-vertex"), "gemini-x", GeminiResponse);

        request.Uri.Host.ShouldEndWith("aiplatform.googleapis.com");
        request.Uri.AbsolutePath.ShouldContain("gemini-x:generateContent");
        response.Text.ShouldBe("OK");
    }

    [Fact]
    public void RegistryBuildsAdapterFromCatalogEntry()
    {
        var entry = new CatalogProvider("newco", "NewCo", "https://api.newco.example/v1", ["NEWCO_API_KEY"], "chat", null);
        var registry = ProviderRegistry.CreateDefault(id => id == "newco" ? entry : null);

        var (adapter, resolved) = registry.Resolve(Connection("newco"));

        adapter.Wire.ShouldBe(WireProtocol.OpenAiChat);
        resolved.Endpoint!.ToString().ShouldBe("https://api.newco.example/v1");
        registry.KeyVariables("newco").ShouldBe(["NEWCO_API_KEY"]);
    }

    [Fact]
    public async Task CatalogProviderEndpointIsUsedOnTheWire()
    {
        var entry = new CatalogProvider("newco", "NewCo", "https://api.newco.example/v1", [], "chat", null);
        var registry = ProviderRegistry.CreateDefault(id => id == "newco" ? entry : null);

        var (request, _) = await Call(Connection("newco"), "m", ChatCompletion, registry);

        request.Uri.ToString().ShouldBe("https://api.newco.example/v1/chat/completions");
    }

    [Fact]
    public void CatalogProtocolIsPreservedWithACustomEndpoint()
    {
        var entry = new CatalogProvider("newco", "NewCo", "https://api.newco.example", [], "anthropic", null);
        var registry = ProviderRegistry.CreateDefault(id => id == "newco" ? entry : null);
        var endpoint = new Uri("https://proxy.example/anthropic");

        var (adapter, resolved) = registry.Resolve(Connection("newco") with { Endpoint = endpoint });

        adapter.Wire.ShouldBe(WireProtocol.Anthropic);
        resolved.Wire.ShouldBe(WireProtocol.Anthropic);
        resolved.Endpoint.ShouldBe(endpoint);
    }

    [Fact]
    public async Task CatalogModelOverridesProtocolEndpointAndOutputLimit()
    {
        var catalog = ModelCatalog.Parse(JsonNode.Parse("""
            {"acme":{"protocol":"chat","api":"https://api.acme.example/v1","models":{
              "m":{"limit":{"output":1024},"provider":{"protocol":"responses","shape":"responses","api":"https://models.acme.example/v1"}}
            }}}
            """));
        var registry = ProviderRegistry.CreateDefault(catalog.FindProvider, catalog.Find);

        var (request, response) = await Call(Connection("acme"), "m", Responses, registry);

        request.Uri.ToString().ShouldBe("https://models.acme.example/v1/responses");
        request.Body!["max_output_tokens"]!.GetValue<int>().ShouldBe(1024);
        response.Text.ShouldBe("OK");
    }

    [Theory]
    [InlineData(1024, 5000, 1024)]
    [InlineData(32768, null, 8192)]
    [InlineData(32768, 1000, 1000)]
    public async Task CatalogLimitsRequestOptionsWithoutMutatingTheCaller(int limit, int? requested, int expected)
    {
        var catalog = ModelCatalog.Parse(JsonNode.Parse("""
            {"acme":{"protocol":"chat","api":"https://api.acme.example/v1","models":{
              "m":{"limit":{"output":1024},"temperature":false}
            }}}
            """));
        var capture = new Capture(ChatCompletion);
        var metadata = catalog.Find("acme", "m")! with { MaxOutputTokens = limit };
        var registry = ProviderRegistry.CreateDefault(catalog.FindProvider, (_, _) => metadata);
        using var client = registry.CreateChatClient(Connection("acme"), "m", capture);
        var options = new ChatOptions { Temperature = 0.7f, MaxOutputTokens = requested };

        await client.GetResponseAsync("hi", options, TestContext.Current.CancellationToken);

        capture.Body!.ContainsKey("temperature").ShouldBeFalse();
        (capture.Body["max_completion_tokens"] ?? capture.Body["max_tokens"])!.GetValue<int>().ShouldBe(expected);
        options.Temperature.ShouldBe(0.7f);
        options.MaxOutputTokens.ShouldBe(requested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedToolsAreRejectedBeforeNormalOrStreamingRequests(bool streaming)
    {
        var catalog = ModelCatalog.Parse(JsonNode.Parse("""
            {"groq":{"models":{"m":{"tool_call":false}}}}
            """));
        using var client = ProviderRegistry.CreateDefault(catalog.FindProvider, catalog.Find)
            .CreateChatClient(Connection("groq"), "m", new Capture(ChatCompletion));
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "sunny", "weather")] };

        await Should.ThrowAsync<NotSupportedException>(async () =>
        {
            if (streaming)
            {
                await foreach (var _ in client.GetStreamingResponseAsync("hi", options, TestContext.Current.CancellationToken)) { }
            }
            else await client.GetResponseAsync("hi", options, TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public async Task UnsupportedImageInputIsRejectedLocally()
    {
        var catalog = ModelCatalog.Parse(JsonNode.Parse("""
            {"groq":{"models":{"m":{"modalities":{"input":["text"],"output":["text"]}}}}}
            """));
        using var client = ProviderRegistry.CreateDefault(catalog.FindProvider, catalog.Find)
            .CreateChatClient(Connection("groq"), "m", new Capture(ChatCompletion));
        var message = new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1, 2 }, "image/png")]);

        await Should.ThrowAsync<NotSupportedException>(() => client.GetResponseAsync([message], cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ExplicitProtocolAndEndpointOverrideCatalogModelRoute()
    {
        var catalog = ModelCatalog.Parse(JsonNode.Parse("""
            {"acme":{"protocol":"anthropic","models":{"m":{"provider":{"shape":"responses","api":"https://other.example/v1"}}}}}
            """));
        var registry = ProviderRegistry.CreateDefault(catalog.FindProvider, catalog.Find);
        var endpoint = new Uri("https://proxy.example/v1");

        var (adapter, resolved) = registry.Resolve(Connection("acme", WireProtocol.OpenAiChat) with { Endpoint = endpoint }, "m");

        adapter.Wire.ShouldBe(WireProtocol.OpenAiChat);
        resolved.Endpoint.ShouldBe(endpoint);
    }

    [Fact]
    public void NativeCloudProvidersDoNotTreatSetupEnvironmentVariablesAsKeys()
    {
        var registry = ProviderRegistry.CreateDefault(id => new CatalogProvider(id, id, null,
            ["AWS_ACCESS_KEY_ID", "AWS_REGION", "GOOGLE_VERTEX_PROJECT"], null, null));

        registry.KeyVariables("amazon-bedrock").ShouldBe(["AWS_BEARER_TOKEN_BEDROCK"]);
        registry.KeyVariables("google-vertex").ShouldBe(["GOOGLE_VERTEX_API_KEY"]);
    }

    [Theory]
    [InlineData("gemini", WireProtocol.Gemini)]
    [InlineData("vertex", WireProtocol.Vertex)]
    [InlineData("bedrock", WireProtocol.Bedrock)]
    [InlineData("chat", WireProtocol.OpenAiChat)]
    [InlineData("responses", WireProtocol.OpenAiResponses)]
    public void Should_SelectSupportedSdkAdapters_When_CatalogDeclaresProtocol(string protocol, WireProtocol wire)
    {
        var entry = new CatalogProvider("acme", "Acme", "https://api.acme.example", [], protocol, null);
        var registry = ProviderRegistry.CreateDefault(_ => entry);

        registry.Resolve(Connection("acme")).Connection.Wire.ShouldBe(wire);
    }

    [Fact]
    public void Should_RejectUnsupportedProtocol_When_CatalogDeclaresUnknownApi()
    {
        var entry = new CatalogProvider("odd", "Odd", "https://odd.example/v1", [], "unsupported-native", null);
        var registry = ProviderRegistry.CreateDefault(id => id == "odd" ? entry : null);

        Should.Throw<NotSupportedException>(() => registry.Resolve(Connection("odd"))).Message.ShouldContain("unsupported-native");
    }

    [Fact]
    public void Should_RequireExplicitProtocol_When_CatalogProviderHasOnlyMetadata()
    {
        var entry = new CatalogProvider("acme", "Acme", "https://api.acme.example/v1", [], null, null);
        var registry = ProviderRegistry.CreateDefault(_ => entry);
        Should.Throw<NotSupportedException>(() => registry.Resolve(Connection("acme"))).Message.ShouldContain("Configure its protocol explicitly");
        registry.Resolve(Connection("acme", WireProtocol.OpenAiChat)).Connection.Wire.ShouldBe(WireProtocol.OpenAiChat);
    }

    [Fact]
    public void UnknownProviderWithoutEndpointIsRejected()
    {
        var registry = ProviderRegistry.CreateDefault();

        Should.Throw<NotSupportedException>(() => registry.Resolve(Connection("nobody")));
    }

    [Fact]
    public void EveryDefaultAdapterHasAUniqueIdAndAConsistentKeyPolicy()
    {
        var adapters = ProviderRegistry.CreateDefault().Adapters;

        adapters.Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count().ShouldBe(adapters.Count);
        foreach (var adapter in adapters)
        {
            (adapter.KeyOptional || adapter.KeyVariables.Count > 0).ShouldBeTrue(adapter.Id);
        }
    }

    private sealed class Capture(string responseJson) : HttpMessageHandler
    {
        private int _active;
        private int _maxActive;

        public Uri Uri { get; private set; } = null!;

        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public JsonObject? Body { get; private set; }

        public bool Stall { get; init; }

        public TimeSpan Delay { get; init; }

        public int MaxActive => _maxActive;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            InterlockedMax(active);
            try
            {
                Uri = request.RequestUri!;
                foreach (var header in request.Headers) Headers[header.Key] = string.Join(",", header.Value);
                if (request.Content is { } content)
                {
                    foreach (var header in content.Headers) Headers[header.Key] = string.Join(",", header.Value);
                    var bytes = await content.ReadAsByteArrayAsync(cancellationToken);
                    if (Headers.TryGetValue("Content-Encoding", out var encoding) && encoding == "gzip")
                    {
                        using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                        using var plain = new MemoryStream();
                        await gzip.CopyToAsync(plain, cancellationToken);
                        bytes = plain.ToArray();
                    }
                    Body = JsonNode.Parse(bytes) as JsonObject;
                }

                if (Stall) await Task.Delay(Timeout.Infinite, cancellationToken);
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
                };
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private void InterlockedMax(int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref _maxActive)) && Interlocked.CompareExchange(ref _maxActive, value, current) != current) { }
        }
    }
}
