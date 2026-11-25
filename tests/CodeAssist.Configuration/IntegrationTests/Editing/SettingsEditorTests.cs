using System.Text.Json.Nodes;
using CodeAssist.Configuration;
using Shouldly;

using CodeAssist.Configuration.IntegrationTests.Support;

namespace CodeAssist.Configuration.IntegrationTests.Editing;

public sealed class SettingsEditorTests : IDisposable
{
    private readonly SettingsFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private (SettingsEditor Editor, SettingsService Service) New()
    {
        var service = new SettingsService(_fx.Locations, new DictionaryEnvironmentVariables(new Dictionary<string, string>()), new FlagOverrides());
        return (new SettingsEditor(service), service);
    }

    [Fact]
    public void RejectedValueReportsReasonOnceNotTwice()
    {
        _fx.User("{\n  \"agent\": {\n    \"maxTurns\": 7\n  }\n}\n");
        var (editor, _) = New();

        var result = editor.Set("agent.maxTurns", "not-a-number", EditScope.User);

        result.Success.ShouldBeFalse();
        result.Diagnostics.ShouldNotBeEmpty();
        result.Diagnostics.Select(d => d.Message).ShouldNotContain(result.Error);
    }

    [Fact]
    public void SetPreservesCommentsAndTouchesOnlyTheKey()
    {
        var original = "{\n  // my notes\n  \"agent\": {\n    \"maxTurns\": 7, // inline\n    \"role\": \"builder\"\n  },\n  \"username\": \"dana\"\n}\n";
        _fx.User(original);
        var (editor, service) = New();

        var result = editor.Set("agent.maxTurns", "11", EditScope.User);

        result.Success.ShouldBeTrue(result.Error);
        var text = File.ReadAllText(Path.Combine(_fx.Home, "settings.json"));
        text.ShouldContain("// my notes");
        text.ShouldContain("11, // inline");
        text.ShouldContain("\"username\": \"dana\"");
        service.Current.Settings.Agent.MaxTurns.ShouldBe(11);
    }

    [Fact]
    public void SetCreatesFileAndNestedObjects()
    {
        var (editor, service) = New();

        editor.Set("providers.work.endpoint", "https://work.example/v1/", EditScope.User).Success.ShouldBeTrue();
        editor.Set("workspace.ignore", "dist,bin", EditScope.User).Success.ShouldBeTrue();

        service.Current.Settings.Providers["work"].Endpoint.ShouldBe("https://work.example/v1/");
        service.Current.Settings.Workspace.Ignore.ShouldContain("dist");
        service.Current.Settings.Workspace.Ignore.ShouldContain("bin");
    }

    [Fact]
    public void UnsetRemovesKeyAndPrunesEmptyParents()
    {
        _fx.User("{\n  \"agent\": {\n    \"maxTurns\": 7\n  },\n  \"username\": \"dana\"\n}\n");
        var (editor, service) = New();

        editor.Unset("agent.maxTurns", EditScope.User).Success.ShouldBeTrue();

        var text = File.ReadAllText(Path.Combine(_fx.Home, "settings.json"));
        text.ShouldNotContain("agent");
        text.ShouldContain("\"username\": \"dana\"");
        service.Current.Settings.Agent.MaxTurns.ShouldBe(20);
    }

    [Fact]
    public void EditsAreRefusedForSecretsLocksAndUntrustedScopes()
    {
        _fx.Managed_("""{ "lock": ["agent.maxTurns"] }""");
        var (editor, _) = New();

        editor.Set("providers.default.apiKey", "sk-x", EditScope.User).Error!.ShouldContain("secret");
        editor.Set("agent.maxTurns", "3", EditScope.User).Error!.ShouldContain("locked");
        editor.Set("providers.x.endpoint", "https://x.example/", EditScope.Project).Error!.ShouldContain("sensitive");
        editor.Set("cli.sessionTabs", "true", EditScope.Project).Error!.ShouldContain("personal");
        editor.Set("nope.key", "1", EditScope.User).Error!.ShouldContain("not a setting");
        editor.Set("agent.maxTurns", "99999", EditScope.User).Success.ShouldBeFalse();
    }

    [Fact]
    public void SemanticallyInvalidEditIsRolledBack()
    {
        _fx.User("{\n  \"username\": \"dana\"\n}\n");
        var before = File.ReadAllText(Path.Combine(_fx.Home, "settings.json"));
        var (editor, service) = New();
        var good = service.Current.Id;

        var result = editor.Set("agent.model", "/", EditScope.User);

        result.Success.ShouldBeFalse();
        File.ReadAllText(Path.Combine(_fx.Home, "settings.json")).ShouldBe(before);
        service.Current.Id.ShouldBe(good);
    }

    [Fact]
    public void PatcherHandlesInlineObjectsAndTrailingCommas()
    {
        var set = JsoncPatcher.Set("""{"a":1,"b":{"c":2,},}""", ["b", "d"], JsonValue.Create(3)!);
        JsonNode.Parse(set, documentOptions: SettingsJson.DocumentOptions)!["b"]!["d"]!.GetValue<int>().ShouldBe(3);

        var removed = JsoncPatcher.Remove("""{"a":1,"b":2}""", ["b"]);
        removed.ShouldBe("""{"a":1}""");
    }

    [Fact]
    public void SchemaDescribesTrustAndRanges()
    {
        var schema = SettingsSchemaGenerator.Generate();

        schema["properties"]!["agent"]!["properties"]!["maxTurns"]!["maximum"]!.GetValue<double>().ShouldBe(1000);
        schema["properties"]!["providers"]!["x-trust"]!.GetValue<string>().ShouldBe("sensitive");
        schema["properties"]!["providers"]!["additionalProperties"]!["properties"]!["apiKey"]!["x-secret"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void ProviderInspectionNeverExposesKeysAndFindsSources()
    {
        var provider = ProviderPresets.Find("openai")!.ToSettings();
        var missing = ProviderInspector.Inspect("openai", provider, new Dictionary<string, string>());
        var conventional = ProviderInspector.Inspect("openai", provider, new Dictionary<string, string> { ["OPENAI_API_KEY"] = "sk" });
        var local = ProviderInspector.Inspect("ollama", ProviderPresets.Find("ollama")!.ToSettings(), new Dictionary<string, string>());

        missing.Usable.ShouldBeFalse();
        conventional.Source.ShouldBe(CredentialSource.ConventionalEnvironment);
        conventional.SourceDetail.ShouldBe("OPENAI_API_KEY");
        local.Source.ShouldBe(CredentialSource.NotRequired);
        local.Usable.ShouldBeTrue();
    }
}
