using System.Text.Json.Nodes;
using System.Net;
using CodeAssist.Models.Catalog;
using CodeAssist.Models.Chat;
using CodeAssist.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Shouldly;

namespace CodeAssist.Server.Composition.IntegrationTests;

public sealed class ProviderCompositionTests
{
    [Fact]
    public void Should_ResolveReferenceWithoutChangingSettings_When_CreatingConnection()
    {
        var settings = new ProviderSettings { ApiKey = "{env:CUSTOM_KEY}" };
        var environment = new Dictionary<string, string> { ["CUSTOM_KEY"] = "fixture-secret", ["OPENAI_API_KEY"] = "fallback-secret" };
        var connection = ProviderComposition.ToConnection("openai", settings, ProviderRegistry.CreateDefault(), environment);
        connection.ApiKey.ShouldBe("fixture-secret");
        settings.ApiKey.ShouldBe("{env:CUSTOM_KEY}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Should_ApplyOverrideOnlyWhenUnlocked_When_UsingLoadedCredentialSettings(bool managed)
    {
        var root = Path.Combine(Path.GetTempPath(), "ca-reference-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var policy = Path.Combine(root, "managed");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(policy);
        try
        {
            var json = managed
                ? """{"lock":["providers.openai.apiKey"],"providers":{"openai":{"apiKey":"{env:EXPLICIT_KEY}"}}}"""
                : """{"providers":{"openai":{"apiKey":"{env:EXPLICIT_KEY}"}}}""";
            File.WriteAllText(Path.Combine(managed ? policy : home, "settings.json"), json);
            var environment = new Dictionary<string, string>
            {
                ["Providers__openai__ApiKey"] = "override-secret", ["OPENAI_API_KEY"] = "fallback-secret", ["EXPLICIT_KEY"] = "referenced-secret",
            };
            var snapshot = SettingsLoader.Load(new(new SettingsLocations(home, root, policy), environment, new Dictionary<string, string>()));
            snapshot.IsValid.ShouldBeTrue();
            var settings = snapshot.Settings.Providers["openai"];
            ProviderComposition.ToConnection("openai", settings, ProviderRegistry.CreateDefault(), environment).ApiKey
                .ShouldBe(managed ? "referenced-secret" : "override-secret");
            ProviderInspector.Inspect("openai", settings, environment).Source
                .ShouldBe(managed ? CredentialSource.ReferencedEnvironment : CredentialSource.SettingsEnvironment);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Should_FailClosedWithoutFallback_When_ExplicitReferenceIsMissing()
    {
        var error = Should.Throw<InvalidOperationException>(() => ProviderComposition.ToConnection("openai", new() { ApiKey = "{env:MISSING_KEY}" },
            ProviderRegistry.CreateDefault(), new Dictionary<string, string> { ["OPENAI_API_KEY"] = "fallback-secret" }));
        error.Message.ShouldContain("MISSING_KEY");
        error.Message.ShouldNotContain("fallback-secret");
    }

    [Fact]
    public void CatalogProviderNeedsOnlyItsIdAndEnvironmentKey()
    {
        var catalog = ModelCatalog.Parse(JsonNode.Parse("""
            {"acme":{"protocol":"anthropic","api":"https://api.acme.example","env":["ACME_API_KEY"],"models":{}}}
            """));
        var registry = ProviderRegistry.CreateDefault(catalog.FindProvider, catalog.Find);

        var connection = ProviderComposition.ToConnection("acme", new ProviderSettings(), registry,
            new Dictionary<string, string> { ["ACME_API_KEY"] = "secret" });
        var resolved = registry.Resolve(connection).Connection;

        resolved.Endpoint.ShouldBe(new Uri("https://api.acme.example"));
        resolved.Wire.ShouldBe(WireProtocol.Anthropic);
        resolved.ApiKey.ShouldBe("secret");
    }

    [Fact]
    public void ExplicitSettingsAndEnvironmentKeyWinOverCatalog()
    {
        var entry = new CatalogProvider("acme", "Acme", "https://api.acme.example", ["ACME_API_KEY"], "anthropic", null);
        var registry = ProviderRegistry.CreateDefault(_ => entry);
        var settings = new ProviderSettings { Protocol = ProviderProtocol.OpenAi, Api = ProviderApi.Chat, Endpoint = "https://proxy.example/v1" };

        var connection = ProviderComposition.ToConnection("acme", settings, registry,
            new Dictionary<string, string> { ["ACME_API_KEY"] = "catalog-key", ["Providers__acme__ApiKey"] = "settings-key" });
        var resolved = registry.Resolve(connection).Connection;

        resolved.Endpoint.ShouldBe(new Uri(settings.Endpoint));
        resolved.Wire.ShouldBe(WireProtocol.OpenAiChat);
        resolved.ApiKey.ShouldBe("settings-key");
    }

    [Theory]
    [InlineData(ProviderProtocol.Gemini, WireProtocol.Gemini)]
    [InlineData(ProviderProtocol.Vertex, WireProtocol.Vertex)]
    [InlineData(ProviderProtocol.Bedrock, WireProtocol.Bedrock)]
    public void NativeProtocolsNeedNoFakeOpenAiEndpoint(ProviderProtocol protocol, WireProtocol wire)
    {
        var connection = ProviderComposition.ToConnection("custom", new ProviderSettings { Protocol = protocol },
            ProviderRegistry.CreateDefault(), new Dictionary<string, string>());

        connection.Endpoint.ShouldBeNull();
        connection.Wire.ShouldBe(wire);
    }

    [Fact]
    public void DisabledProviderCannotCreateAClient()
    {
        Should.Throw<InvalidOperationException>(() => ProviderComposition.CreateChatClient("groq", "m",
            new ProviderSettings { Enabled = false }, ProviderRegistry.CreateDefault(), new Dictionary<string, string>()));
    }

    [Fact]
    public async Task ModelAliasesAndRequestOverlaysReachTheProvider()
    {
        var settings = new ProviderSettings
        {
            Endpoint = "https://gateway.example/v1", Protocol = ProviderProtocol.OpenAi,
            Headers = new() { ["X-Team"] = "provider" },
            Body = new() { ["metadata"] = "{\"provider\":true}" },
            Models = new()
            {
                ["friendly"] = new ModelSettings
                {
                    ModelId = "upstream/model", MaxOutputTokens = 512,
                    Headers = new() { ["X-Team"] = "model" },
                    Body = new() { ["metadata"] = "{\"model\":true}" },
                },
            },
        };
        var handler = new Capture();
        using var client = ProviderComposition.CreateChatClient("gateway", "friendly", settings, ProviderRegistry.CreateDefault(),
            new Dictionary<string, string> { ["Providers__gateway__ApiKey"] = "secret" }, handler);

        await client.GetResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken);

        handler.Model.ShouldBe("upstream/model");
        handler.Team.ShouldBe("model");
        handler.MaxTokens.ShouldBe(512);
        handler.Metadata!["model"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task Should_ApplyModelVariantWithoutMutatingSettings_When_CreatingAClient()
    {
        var settings = new ProviderSettings
        {
            Endpoint = "https://gateway.example/v1", Protocol = ProviderProtocol.OpenAi, Api = ProviderApi.Chat, ApiKey = "fixture-only",
            MaxOutputTokens = 512, Headers = new() { ["X-Team"] = "original" },
            Variants = new() { ["review"] = new() { Body = new() { ["metadata"] = "{\"level\":\"provider\"}" } } },
            Models = new() { ["friendly"] = new() { Variants = new() { ["review"] = new() { Headers = new() { ["X-Team"] = "variant" }, Body = new() { ["metadata"] = "{\"level\":\"model\"}" }, Effort = "high" } } } },
        };
        var handler = new Capture();
        using var client = ProviderComposition.CreateChatClient("gateway", "friendly", settings, ProviderRegistry.CreateDefault(), new Dictionary<string, string>(), handler, "review");
        await client.GetResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken);
        handler.Team.ShouldBe("variant");
        handler.Metadata!["level"]!.GetValue<string>().ShouldBe("model");
        handler.Effort.ShouldBe("high");
        settings.Headers["X-Team"].ShouldBe("original");
        settings.Body.ShouldBeEmpty();
        Should.Throw<InvalidOperationException>(() => ProviderComposition.CreateChatClient("gateway", "friendly", settings, ProviderRegistry.CreateDefault(), new Dictionary<string, string>(), variant: "unknown"));
    }

    [Fact]
    public async Task Should_KeepAliasCatalogLimits_When_SendingAnUpstreamModelId()
    {
        var catalog = ModelCatalog.Parse(JsonNode.Parse("""
            {"gateway":{"protocol":"chat","models":{"friendly":{"limit":{"output":64}}}}}
            """));
        var settings = new ProviderSettings
        {
            Endpoint = "https://gateway.example/v1", Api = ProviderApi.Chat, ApiKey = "fixture-only",
            Headers = new() { ["X-Team"] = "fixture" },
            Models = new() { ["friendly"] = new() { ModelId = "upstream/model", MaxOutputTokens = 512 } },
        };
        var handler = new Capture();
        using var client = ProviderComposition.CreateChatClient("gateway", "friendly", settings,
            ProviderRegistry.CreateDefault(catalog.FindProvider, catalog.Find), new Dictionary<string, string>(), handler);
        await client.GetResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken);
        handler.Model.ShouldBe("upstream/model");
        handler.MaxTokens.ShouldBe(64);
    }

    [Fact]
    public async Task Should_EnforceConfiguredCapabilities_When_ModelHasNoPublishedMetadata()
    {
        var settings = new ProviderSettings
        {
            Endpoint = "https://gateway.example/v1", Api = ProviderApi.Chat, ApiKey = "fixture-only",
            Models = new() { ["restricted"] = new() { Tools = false, MaxOutputTokens = 64 } },
        };
        using var client = ProviderComposition.CreateChatClient("gateway", "restricted", settings, ProviderRegistry.CreateDefault(), new Dictionary<string, string>());
        await Should.ThrowAsync<NotSupportedException>(() => client.GetResponseAsync("hi", new ChatOptions { Tools = [AIFunctionFactory.Create(() => "ok", "fixture")] }, TestContext.Current.CancellationToken));
        settings.Models["restricted"].Type = ModelType.Embedding;
        Should.Throw<NotSupportedException>(() => ProviderComposition.CreateChatClient("gateway", "restricted", settings, ProviderRegistry.CreateDefault(), new Dictionary<string, string>()));
    }

    [Fact]
    public void CloudEnvironmentAliasesConfigureOptionsWithoutBecomingKeys()
    {
        var environment = new Dictionary<string, string>
        {
            ["AWS_DEFAULT_REGION"] = "eu-west-1", ["AWS_PROFILE"] = "dev",
            ["GOOGLE_VERTEX_PROJECT"] = "my-project", ["GOOGLE_VERTEX_LOCATION"] = "global",
        };
        var registry = ProviderRegistry.CreateDefault();
        var aws = ProviderComposition.ToConnection("amazon-bedrock", new ProviderSettings(), registry, environment);
        var google = ProviderComposition.ToConnection("google-vertex", new ProviderSettings(), registry, environment);

        aws.ApiKey.ShouldBeNull();
        aws.Options["region"].ShouldBe("eu-west-1");
        aws.Options["profile"].ShouldBe("dev");
        google.ApiKey.ShouldBeNull();
        google.Options["project"].ShouldBe("my-project");
        google.Options["location"].ShouldBe("global");
    }

    [Fact]
    public void ProviderRegistrationUsesTheSharedCatalogService()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ca-composition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, ModelCatalogClient.FeedFileName), """
                {"acme":{"protocol":"anthropic","api":"https://api.acme.example","models":{"m":{}}}}
                """);
            using var http = new HttpClient();
            var catalog = new ModelCatalogService(new ModelCatalogClient(http), directory, () => "https://models.example/api.json");
            var services = new ServiceCollection();
            services.AddSingleton(catalog);
            services.AddCodeAssistProviders();
            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<ProviderRegistry>().Resolve(new ProviderConnection { Id = "acme" }).Connection.Wire.ShouldBe(WireProtocol.Anthropic);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class Capture : HttpMessageHandler
    {
        public string? Model { get; private set; }
        public string? Team { get; private set; }
        public int? MaxTokens { get; private set; }
        public JsonNode? Metadata { get; private set; }
        public string? Effort { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            Model = body["model"]!.GetValue<string>();
            Team = request.Headers.GetValues("X-Team").Single();
            MaxTokens = (body["max_completion_tokens"] ?? body["max_tokens"])!.GetValue<int>();
            Metadata = body["metadata"];
            Effort = body["reasoning_effort"]?.GetValue<string>();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"id":"c1","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}]}
                    """),
            };
        }
    }
}