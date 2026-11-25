using System.ClientModel;
using System.Text.Json;
using CodeAssist.Testing.Live;
using Microsoft.Extensions.AI;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace CodeAssist.Models.IntegrationTests.Chat;

public sealed class LiveProviderConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ca-live-settings-" + Guid.NewGuid().ToString("N"));

    public LiveProviderConfigurationTests() => Directory.CreateDirectory(Path.Combine(_root, "home"));
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Dictionary<string, string> Environment() => new()
    {
        ["ENABLED_LIVE_TESTS"] = "1",
        ["CODE_ASSIST_HOME"] = Path.Combine(_root, "home"),
        ["CODE_ASSIST_MANAGED_DIR"] = Path.Combine(_root, "managed"),
    };

    [Theory]
    [InlineData("openai", false)]
    [InlineData("anthropic", false)]
    [InlineData("openai", true)]
    [InlineData("anthropic", true)]
    public async Task Should_ApplySettingsAndEnvironment_When_CreatingProductionSdkClient(string protocol, bool dotenv)
    {
        using var server = WireMockServer.Start();
        var responseBody = protocol == "anthropic"
            ? """{"id":"msg-fixture","type":"message","role":"assistant","model":"deepseek-flash","content":[{"type":"text","text":"OK"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}"""
            : """{"id":"chatcmpl-fixture","object":"chat.completion","created":1,"model":"deepseek-flash","choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";
        server.Given(Request.Create().WithPath("/api/*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(responseBody));
        var saved = """
            {"agent":{"model":"openai-byok/friendly"},"providers":{
              "default":{"enabled":false},
              "openai-byok":{"protocol":"PROTOCOL","api":"chat","endpoint":"https://saved.invalid/api/",
                "apiKey":"{env:DEEPSEEK_API_KEY}","headers":{"X-Team":"saved"},"maxOutputTokens":128,
                "models":{"friendly":{"modelId":"deepseek-flash","maxOutputTokens":64,"tools":true},
                  "disabled":{"enabled":false},"image":{"type":"image"}}},
              "off":{"enabled":false,"models":{"never":{}}}}}
            """.Replace("PROTOCOL", protocol, StringComparison.Ordinal);
        var path = Path.Combine(_root, "home", "settings.json");
        if (dotenv) saved = saved.Replace("\"apiKey\":\"{env:DEEPSEEK_API_KEY}\",", "", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, saved, TestContext.Current.CancellationToken);
        var environment = Environment();
        if (dotenv)
        {
            File.WriteAllText(Path.Combine(_root, "CodeAssist.slnx"), "<Solution />");
            File.WriteAllText(Path.Combine(_root, ".env"), "Providers__openai-byok__ApiKey={env:DEEPSEEK_API_KEY}\nDEEPSEEK_API_KEY=fixture-key\n");
        }
        else environment["DEEPSEEK_API_KEY"] = "fixture-key";
        environment["Providers__openai-byok__Endpoint"] = server.Url + "/api/";
        environment["Providers__openai-byok__Headers__X-Team"] = "environment";
        environment["Providers__openai-byok__Models__friendly__Body__thinking"] = """{"type":"disabled"}""";
        var configured = LiveProviderConfiguration.Load(dotenv ? LiveTestConfiguration.Load(environment, _root) : new(environment), _root);
        configured.Models.Count.ShouldBe(1);
        configured.Models.Single().ModelId.ShouldBe("friendly");
        configured.Models.Single().Tools.ShouldBeTrue();
        using var client = configured.CreateClient("openai-byok", "friendly");

        var response = await client.GetResponseAsync("Reply OK", cancellationToken: TestContext.Current.CancellationToken);

        response.Text.ShouldBe("OK");
        server.LogEntries.ShouldHaveSingleItem();
        server.FindLogEntries(Request.Create().WithHeader("X-Team", "environment")).ShouldHaveSingleItem();
        server.FindLogEntries(protocol == "anthropic"
            ? Request.Create().WithHeader("x-api-key", "fixture-key")
            : Request.Create().WithHeader("Authorization", "Bearer fixture-key")).ShouldHaveSingleItem();
        var request = server.LogEntries.Single().RequestMessage.ShouldNotBeNull();
        request.Path.ShouldNotBeNull().ShouldStartWith("/api/");
        using var json = JsonDocument.Parse(request.Body!);
        json.RootElement.GetProperty("model").GetString().ShouldBe("deepseek-flash");
        var maximum = json.RootElement.TryGetProperty("max_tokens", out var tokens)
            ? tokens.GetInt32() : json.RootElement.GetProperty("max_completion_tokens").GetInt32();
        maximum.ShouldBe(64);
        json.RootElement.GetProperty("thinking").GetProperty("type").GetString().ShouldBe("disabled");
        File.ReadAllText(path).ShouldBe(saved);
    }

    [Fact]
    public void Should_SelectEnabledModelsAndCapabilities_When_SettingsComeFromEnvironment()
    {
        var environment = Environment();
        environment["Agent__Model"] = "openai-byok/deepseek-flash";
        environment["Providers__openai-byok__Protocol"] = "openai";
        environment["Providers__openai-byok__Endpoint"] = "https://api.deepseek.com";
        environment["Providers__openai-byok__ApiKey"] = "fixture-key";
        environment["Providers__openai-byok__Models__deepseek-flash__Tools"] = "false";
        environment["Providers__openai-byok__Models__deepseek-flash__Input__0"] = "text";
        var configured = LiveProviderConfiguration.Load(new(environment), _root);

        configured.Models.ShouldHaveSingleItem();
        configured.Models.Single().ProviderId.ShouldBe("openai-byok");
        configured.Models.Single().Tools.ShouldBeFalse();
        Directory.GetFiles(_root, "settings.json", SearchOption.AllDirectories).ShouldBeEmpty();
        environment["ENABLED_LIVE_TESTS"] = "0";
        var disabled = LiveProviderConfiguration.Load(new(environment), _root);
        Should.Throw<InvalidOperationException>(() => disabled.CreateClient("openai-byok", "deepseek-flash"));
    }

    [Fact]
    public void Should_SelectAgentModel_When_ProviderHasNoExplicitModelMap()
    {
        var environment = Environment();
        environment["Agent__Model"] = "ollama/local-model";
        environment["Providers__ollama__Endpoint"] = "http://localhost:11434/v1/";

        var configured = LiveProviderConfiguration.Load(new(environment), _root);

        configured.Models.Single().ModelId.ShouldBe("local-model");
        configured.Models.Single().ProviderId.ShouldBe("ollama");
    }

    [Fact]
    public void Should_DisableLiveRequests_When_ProcessOverridesDotEnv()
    {
        File.WriteAllText(Path.Combine(_root, "CodeAssist.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(_root, ".env"), """
            ENABLED_LIVE_TESTS=1
            Agent__Model=openai-byok/deepseek-flash
            Providers__default__Enabled=false
            Providers__openai-byok__Protocol=openai
            Providers__openai-byok__Api=chat
            Providers__openai-byok__Endpoint=https://api.deepseek.com
            Providers__openai-byok__ApiKey={env:DEEPSEEK_API_KEY}
            Providers__openai-byok__Models__deepseek-flash__Tools=true
            DEEPSEEK_API_KEY=fixture-key
            """);
        var environment = Environment();
        environment["ENABLED_LIVE_TESTS"] = "0";
        var configuration = LiveTestConfiguration.Load(environment, _root);

        var providers = LiveProviderConfiguration.Load(configuration);

        configuration.Enabled.ShouldBeFalse();
        providers.Models.Single().ProviderId.ShouldBe("openai-byok");
        providers.Models.Single().ModelId.ShouldBe("deepseek-flash");
        providers.Settings.Providers["openai-byok"].Endpoint.ShouldBe("https://api.deepseek.com");
        Should.Throw<InvalidOperationException>(() => providers.CreateClient("openai-byok", "deepseek-flash"));
    }

    [Fact]
    public void Should_ReportSafeDiagnostics_When_SettingsAreInvalid()
    {
        var environment = Environment();
        environment["Providers__default__TimeoutSeconds"] = "sensitive-invalid-value";

        var exception = Should.Throw<InvalidOperationException>(() => LiveProviderConfiguration.Load(new(environment), _root));

        exception.Message.ShouldContain("invalid_value");
        exception.Message.ShouldNotContain("sensitive-invalid-value");
    }

    [Fact]
    public async Task Should_PropagateAuthenticationFailure_When_ProviderRejectsCredential()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/v1/chat/completions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401).WithHeader("Content-Type", "application/json")
                .WithBody("""{"error":{"message":"Invalid fixture credential","type":"invalid_request_error","code":"invalid_api_key"}}"""));
        var environment = Environment();
        environment["Providers__openai-byok__Protocol"] = "openai";
        environment["Providers__openai-byok__Api"] = "chat";
        environment["Providers__openai-byok__Endpoint"] = server.Url + "/v1/";
        environment["Providers__openai-byok__ApiKey"] = "rejected-fixture-key";
        environment["Providers__openai-byok__Models__deepseek-flash__Tools"] = "true";
        var configured = LiveProviderConfiguration.Load(new(environment), _root);
        using var client = configured.CreateClient("openai-byok", "deepseek-flash");

        var exception = await Should.ThrowAsync<ClientResultException>(() => client.GetResponseAsync(
            "Reply OK", cancellationToken: TestContext.Current.CancellationToken));

        exception.Status.ShouldBe(401);
        server.LogEntries.ShouldHaveSingleItem();
    }
}