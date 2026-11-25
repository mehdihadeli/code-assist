using CodeAssist.Configuration;
using Shouldly;

using CodeAssist.Configuration.IntegrationTests.Support;

namespace CodeAssist.Configuration.IntegrationTests.Model;

public sealed class ProviderModelSettingsTests : IDisposable
{
    private readonly SettingsFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void CatalogProvidersNeedNoEndpointAndSupportEnvironmentProtocolOverrides()
    {
      _fx.User("""{ "providers": { "acme": { "apiKey": "{env:ACME_API_KEY}" } } }""");

      var snapshot = _fx.Load(new Dictionary<string, string> { ["Providers__acme__Protocol"] = "gemini" });

      snapshot.IsValid.ShouldBeTrue(string.Join("; ", snapshot.Diagnostics.Select(d => d.Message)));
      snapshot.Settings.Providers["acme"].Endpoint.ShouldBe("");
      snapshot.Settings.Providers["acme"].Protocol.ShouldBe(ProviderProtocol.Gemini);
    }

    [Fact]
    public void GatewayProviderWithCompatibilityAndReasoningLoads()
    {
        _fx.User("""
        {
          "agent": { "model": "acme/coder" },
          "providers": {
            "acme": {
              "displayName": "Acme",
              "apiKey": "{env:ACME_API_KEY}",
              "endpoint": "https://llm.acme.example/v1/",
              "canonical": "openai",
              "encoding": "gzip",
              "transport": "websocket",
              "compaction": "native",
              "headerTimeoutSeconds": 600,
              "body": { "metadata": "{\"app\":\"code-assist\"}" },
              "models": {
                "coder": {
                  "modelId": "upstream/coder-v2",
                  "family": "coder",
                  "type": "chat",
                  "releaseDate": "2026-03",
                  "inputTokenLimit": 100000,
                  "compatibility": { "reasoningField": "reasoning_content", "cacheFormat": "anthropic", "streamUsage": false },
                  "reasoningControls": { "toggle": true, "efforts": ["low", "high"] }
                }
              }
            }
          }
        }
        """);

        var snapshot = _fx.Load();

        snapshot.IsValid.ShouldBeTrue(string.Join("; ", snapshot.Diagnostics.Select(d => d.Message)));
        var provider = snapshot.Settings.Providers["acme"];
        provider.Encoding.ShouldBe(RequestEncoding.Gzip);
        provider.Transport.ShouldBe(ProviderTransport.WebSocket);
        provider.Compaction.ShouldBe(CompactionStrategy.Native);
        var model = provider.Models["coder"];
        model.Compatibility.CacheFormat.ShouldBe(CacheFormat.Anthropic);
        model.Compatibility.StreamUsage.ShouldBe(false);
        model.Compatibility.RequireFinishReason.ShouldBeNull();
        model.ReasoningControls.Efforts.ShouldBe(["low", "high"]);
    }

    [Fact]
    public void EnvironmentReachesNestedCompatibilityFields()
    {
        _fx.User("""{ "providers": { "local": { "endpoint": "http://127.0.0.1:1234/v1/", "models": { "m": {} } } } }""");

        var snapshot = _fx.Load(new Dictionary<string, string>
        {
            ["Providers__local__Models__m__Compatibility__MaxTokensField"] = "max_tokens",
            ["Providers__local__ApiKey"] = "{env:LOCAL_KEY}",
        });

        snapshot.Settings.Providers["local"].Models["m"].Compatibility.MaxTokensField.ShouldBe("max_tokens");
        snapshot.Settings.Providers["local"].ApiKey.ShouldBe("{env:LOCAL_KEY}");
    }

    [Fact]
    public void Should_ResolveCredentialReference_When_InspectingProvider()
    {
      var provider = new ProviderSettings { Endpoint = "https://llm.acme.example/v1/", ApiKey = "{env:ACME_API_KEY}" };

        var status = ProviderInspector.Inspect("acme", provider, new Dictionary<string, string> { ["ACME_API_KEY"] = "k" });

        status.Usable.ShouldBeTrue();
        status.Source.ShouldBe(CredentialSource.ReferencedEnvironment);
        status.SourceDetail.ShouldBe("ACME_API_KEY");
    }

    [Fact]
    public void InvalidBodyJsonAndBudgetRangeAreRejected()
    {
        _fx.User("""
        {
          "providers": {
            "default": {
              "body": { "metadata": "{not json" },
              "models": { "m": { "reasoningControls": { "budgetMin": 9000, "budgetMax": 1000 } } }
            }
          }
        }
        """);

        var codes = _fx.Load().Diagnostics.Select(d => d.Code).ToList();

        codes.ShouldContain("invalid_json");
        codes.ShouldContain("reasoning_budget");
    }

      [Theory]
      [InlineData(false)]
      [InlineData(true)]
      public void Should_KeepReferenceUnresolvedAndMasked_When_LoadingTrustedFiles(bool managed)
      {
        const string json = """{"providers":{"acme":{"apiKey":"{env:ACME_API_KEY}"}}}""";
        if (managed) _fx.Managed_(json); else _fx.User(json);
        var snapshot = _fx.Load(new() { ["ACME_API_KEY"] = "fixture-secret" });
        snapshot.IsValid.ShouldBeTrue();
        snapshot.Settings.Providers["acme"].ApiKey.ShouldBe("{env:ACME_API_KEY}");
        snapshot.Redacted().ToJsonString().ShouldNotContain("ACME_API_KEY");
        snapshot.Redacted().ToJsonString().ShouldNotContain("fixture-secret");
        string.Join(" ", snapshot.Diagnostics.Select(item => item.Message)).ShouldNotContain("fixture-secret");
        File.ReadAllText(Path.Combine(managed ? _fx.Managed : _fx.Home, "settings.json")).ShouldBe(json);
      }

      [Theory]
      [InlineData("literal-sensitive-key")]
      [InlineData("prefix{env:KEY}")]
      [InlineData("{env:KEY}suffix")]
      public void Should_RejectLiteralOrInterpolatedSecrets_When_LoadingFiles(string value)
      {
        _fx.User(System.Text.Json.JsonSerializer.Serialize(new { providers = new { acme = new { apiKey = value } } }));
        var snapshot = _fx.Load();
        snapshot.Settings.Providers["acme"].ApiKey.ShouldBeNull();
        snapshot.Diagnostics.ShouldContain(item => item.Code == "secret_rejected");
        string.Join(" ", snapshot.Diagnostics.Select(item => item.Message)).ShouldNotContain(value);
      }

      [Fact]
      public void Should_RejectCredentialRedirection_When_ProjectOrFlagsSupplyReferences()
      {
        _fx.User("""{"providers":{"acme":{"apiKey":"{env:TRUSTED_KEY}"}}}""");
        _fx.Project("""{"providers":{"acme":{"apiKey":"{env:PROJECT_KEY}"}}}""");
        var snapshot = _fx.Load(flags: new() { ["providers.acme.apiKey"] = "{env:FLAG_KEY}" });
        snapshot.Settings.Providers["acme"].ApiKey.ShouldBe("{env:TRUSTED_KEY}");
        snapshot.Diagnostics.ShouldContain(item => item.Code == "secret_rejected");
      }

      [Fact]
      public void Should_RemoveEnvsFromSchema_When_ObsoleteConfigurationIsLoaded()
      {
        _fx.User("""{"providers":{"acme":{"envs":["ACME_API_KEY"]}}}""");
        var snapshot = _fx.Load();
        snapshot.Diagnostics.ShouldContain(item => item.Code == "unknown_key" && item.Path == "providers.acme.envs");
        typeof(ProviderSettings).GetProperty("Envs").ShouldBeNull();
      }

      [Fact]
      public void Should_ReportMissingReferenceWithoutFallback_When_InspectingProvider()
      {
        var provider = new ProviderSettings { Endpoint = "https://example.test", ApiKey = "{env:EXPLICIT_KEY}" };
        var status = ProviderInspector.Inspect("openai", provider, new Dictionary<string, string> { ["OPENAI_API_KEY"] = "fixture-secret" });
        status.Usable.ShouldBeFalse();
        status.Source.ShouldBe(CredentialSource.Missing);
        status.SourceDetail.ShouldBe("EXPLICIT_KEY");
        status.Problem!.ShouldNotContain("fixture-secret");
      }

      [Fact]
      public void Should_MaskRejectedSecretAndKeepReference_When_ManagedLockDeniesOverride()
      {
        _fx.Managed_("""{"lock":["providers.acme.apiKey"],"providers":{"acme":{"apiKey":"{env:MANAGED_KEY}"}}}""");
        var snapshot = _fx.Load(new() { ["Providers__acme__ApiKey"] = "rejected-sensitive-value" });
        snapshot.Settings.Providers["acme"].ApiKey.ShouldBe("{env:MANAGED_KEY}");
        System.Text.Json.JsonSerializer.Serialize(snapshot.Origins).ShouldNotContain("rejected-sensitive-value");
        string.Join(" ", snapshot.Diagnostics.Select(item => item.Message)).ShouldNotContain("rejected-sensitive-value");
      }
}
