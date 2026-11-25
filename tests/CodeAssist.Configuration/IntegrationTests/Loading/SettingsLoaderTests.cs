using System.Text.Json.Nodes;
using CodeAssist.Configuration;
using Shouldly;

using CodeAssist.Configuration.IntegrationTests.Support;

namespace CodeAssist.Configuration.IntegrationTests.Loading;

public sealed class SettingsLoaderTests : IDisposable
{
    private readonly SettingsFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void Should_KeepPluginGovernanceManaged_When_UserProjectAndEnvironmentTryToOverride()
    {
        _fx.Managed_("""{"policy":{"plugins":{"ids":[],"localPackages":false}}}""");
        _fx.User("""{"policy":{"plugins":{"localPackages":true}}}""");
        _fx.Project("""{"pluginLibraries":{"unsafe":{"source":"https://example.test/catalog.json"}},"policy":{"plugins":{"localPackages":true}}}""");
        var snapshot = _fx.Load(new() { ["Policy__Plugins__LocalPackages"] = "true", ["PluginLibraries__team__Source"] = "https://example.test/reviewed.json" });
        snapshot.Settings.Policy.Plugins.LocalPackages.ShouldBeFalse();
        snapshot.Settings.Policy.Plugins.Ids.ShouldNotBeNull().ShouldBeEmpty();
        snapshot.Settings.PluginLibraries.ShouldNotContainKey("unsafe");
        snapshot.Settings.PluginLibraries["team"].Source.ShouldBe("https://example.test/reviewed.json");
        File.Exists(Path.Combine(_fx.Home, "settings.json")).ShouldBeTrue();
        File.ReadAllText(Path.Combine(_fx.Home, "settings.json")).ShouldNotContain("reviewed.json");
    }

    [Fact]
    public void DefaultsLoadWithoutFiles()
    {
        var snapshot = _fx.Load();

        snapshot.IsValid.ShouldBeTrue(string.Join("; ", snapshot.Diagnostics.Select(d => d.Message)));
        snapshot.Settings.Agent.MaxTurns.ShouldBe(20);
        snapshot.OriginOf("agent.maxTurns").Tier.ShouldBe(SettingsTier.Defaults);
    }

    [Fact]
    public void Should_ExposeOnlyTerminalPresentationSettings_When_CatalogIsBuilt()
    {
        var sections = KeyCatalog.Default.Root.Children;

        sections.ShouldContainKey("cli");
        sections.ShouldContainKey("appearance");
        sections.ShouldNotContainKey("web");
        sections.ShouldNotContainKey("desktop");
        _fx.Load().Settings.Tools.Web.ShouldNotBeNull();
    }

    [Fact]
    public void HigherTiersWinAndRecordProvenance()
    {
        _fx.User("""{ "agent": { "maxTurns": 12 } }""");
        var env = new Dictionary<string, string> { ["Agent__MaxTurns"] = "8" };
        var flags = new Dictionary<string, string> { ["agent.maxTurns"] = "5" };

        var snapshot = _fx.Load(env, flags);

        snapshot.Settings.Agent.MaxTurns.ShouldBe(5);
        var origin = snapshot.OriginOf("agent.maxTurns");
        origin.Tier.ShouldBe(SettingsTier.Flags);
        origin.Candidates.Select(c => c.Tier).ShouldBe([SettingsTier.User, SettingsTier.Environment, SettingsTier.Flags]);
    }

    [Fact]
    public void EnvironmentUsesDoubleUnderscoreForDictionariesAndListsCaseInsensitively()
    {
        _fx.User("""{ "providers": { "Work": { "endpoint": "https://work.example/v1/" } } }""");
        var env = new Dictionary<string, string>
        {
            ["PROVIDERS__WORK__TIMEOUTSECONDS"] = "30",
            ["Workspace__Ignore__0"] = "dist",
            ["Workspace__Ignore__1"] = "bin",
        };

        var snapshot = _fx.Load(env);

        snapshot.Settings.Providers.ShouldContainKey("Work");
        snapshot.Settings.Providers.Keys.Count(k => string.Equals(k, "work", StringComparison.OrdinalIgnoreCase)).ShouldBe(1);
        snapshot.Settings.Providers["Work"].TimeoutSeconds.ShouldBe(30);
        snapshot.Settings.Providers["Work"].Endpoint.ShouldBe("https://work.example/v1/");
        snapshot.Settings.Workspace.Ignore.ShouldBe(["bin/**", "obj/**", "node_modules/**", "dist", "bin"]);
    }

    [Fact]
    public void UnrelatedEnvironmentVariablesAreIgnored()
    {
        var snapshot = _fx.Load(new Dictionary<string, string> { ["PATH"] = "x", ["SOME__THING"] = "y", ["CODE_ASSIST_HOME"] = "z" });

        snapshot.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void EnvironmentAcceptsWholeSectionsAndMoreSpecificOverridesWithoutWritingFiles()
    {
        const string saved = """{"agent":{"maxTurns":12},"providers":{"work":{"endpoint":"https://saved.example/v1","headers":{"X-Team":"saved"}}}}""";
        _fx.User(saved);
        var snapshot = _fx.Load(new()
        {
            ["Agent"] = """{"maxTurns":8,"model":"work/deepseek-flash"}""",
            ["Providers"] = """{"work":{"protocol":"openai","api":"chat","endpoint":"https://api.deepseek.com","models":{"deepseek-flash":{"tools":false}}}}""",
            ["Providers__work__ApiKey"] = "test-secret",
            ["Providers__work__Models__deepseek-flash__Tools"] = "true",
            ["Providers__work__Models__deepseek-flash__Input__0"] = "text",
            ["Providers__work__Models__deepseek-flash__Input__1"] = "image",
            ["Providers__work__Options__custom"] = "value",
            ["Providers__work__Headers__X-Team"] = "environment",
            ["Tools__Disabled"] = "fs.write,proc.run",
        });

        snapshot.IsValid.ShouldBeTrue(string.Join("; ", snapshot.Diagnostics.Select(diagnostic => diagnostic.Code)));
        snapshot.Settings.Agent.MaxTurns.ShouldBe(8);
        snapshot.Settings.Agent.Model.ShouldBe("work/deepseek-flash");
        var provider = snapshot.Settings.Providers["work"];
        provider.Endpoint.ShouldBe("https://api.deepseek.com");
        provider.ApiKey.ShouldBe("test-secret");
        provider.Models["deepseek-flash"].Tools.ShouldBeTrue();
        provider.Models["deepseek-flash"].Input.ShouldBe([Modality.Text, Modality.Image]);
        provider.Options["custom"].ShouldBe("value");
        provider.Headers["X-Team"].ShouldBe("environment");
        snapshot.Settings.Tools.Disabled.ShouldBe(["fs.write", "proc.run"]);
        File.ReadAllText(Path.Combine(_fx.Home, "settings.json")).ShouldBe(saved);
        snapshot.Redacted()["providers"]!["work"]!["apiKey"]!.GetValue<string>().ShouldBe("(secret)");
    }

    [Fact]
    public void WholeSectionEnvironmentOverridesStillRespectManagedLocks()
    {
        _fx.Managed_("""{"lock":["agent.maxTurns"],"agent":{"maxTurns":6}}""");
        var snapshot = _fx.Load(new() { ["Agent"] = """{"maxTurns":50,"maxTokens":1234}""" });

        snapshot.Settings.Agent.MaxTurns.ShouldBe(6);
        snapshot.Settings.Agent.MaxTokens.ShouldBe(1234);
        snapshot.OriginOf("agent.maxTurns").Tier.ShouldBe(SettingsTier.Managed);
    }

    [Fact]
    public void EveryNonManagedSchemaPropertyCanBeAddressedThroughEnvironment()
    {
        var environment = new Dictionary<string, string>();
        var origins = new HashSet<string>(StringComparer.Ordinal);
        Add(KeyCatalog.Default.Root, [], null);

        var snapshot = _fx.Load(environment);

        environment.Count.ShouldBeGreaterThan(200);
        snapshot.Diagnostics.ShouldNotContain(diagnostic => new[] { "unknown_key", "type_mismatch", "invalid_value", "out_of_range", "bind_error" }.Contains(diagnostic.Code));
        foreach (var path in origins) snapshot.OriginOf(path).Tier.ShouldBe(SettingsTier.Environment, path);

        void Add(KeyNode node, List<string> path, string? listOrigin)
        {
            if (node.Trust == TrustClass.ManagedOnly) return;
            switch (node.Kind)
            {
                case KeyKind.Object:
                    foreach (var child in node.Children.Values) Add(child, [.. path, child.Name], listOrigin);
                    break;
                case KeyKind.Dictionary:
                    Add(node.Element!, [.. path, "sample"], listOrigin);
                    break;
                case KeyKind.List:
                    Add(node.Element!, [.. path, "0"], listOrigin ?? SettingsPath.Format(path));
                    break;
                default:
                    environment[string.Join("__", path)] = node.IsEnum ? node.EnumNames[0]
                        : node.ScalarType == typeof(bool) ? "true"
                        : node.IsNumeric ? (node.Min ?? 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : "sample";
                    origins.Add(listOrigin ?? SettingsPath.Format(path));
                    break;
            }
        }
    }

    [Fact]
    public void ProjectFileCannotIntroduceProvidersConnectorsOrLooserLimits()
    {
        _fx.User("""{ "agent": { "maxTurns": 10, "permissionPreset": "ask" } }""");
        _fx.Project("""
        {
          "providers": { "evil": { "endpoint": "https://evil.example/" } },
          "connectors": { "servers": { "x": { "command": "calc" } } },
          "agent": { "maxTurns": 500, "permissionPreset": "acceptEdits" }
        }
        """);

        var snapshot = _fx.Load();

        snapshot.Settings.Providers.ShouldNotContainKey("evil");
        snapshot.Settings.Connectors.Servers.ShouldBeEmpty();
        snapshot.Settings.Agent.MaxTurns.ShouldBe(10);
        snapshot.Settings.Agent.PermissionPreset.ShouldBe(PermissionPreset.Ask);
        snapshot.OriginOf("agent.maxTurns").Candidates.Last().Accepted.ShouldBeFalse();
    }

    [Fact]
    public void ProjectFileMayTightenLimitsAndAddDenyRulesOnly()
    {
        _fx.User("""{ "agent": { "maxTurns": 10, "permissionPreset": "ask" } }""");
        _fx.Project("""
        {
          "agent": { "maxTurns": 4, "permissionPreset": "readOnly" },
          "rules": [
            { "capability": "fs.write", "decision": "allow", "match": { "path": "**" } },
            { "capability": "proc.exec", "decision": "deny", "match": { "command": "rm *" } }
          ]
        }
        """);

        var snapshot = _fx.Load();

        snapshot.Settings.Agent.MaxTurns.ShouldBe(4);
        snapshot.Settings.Agent.PermissionPreset.ShouldBe(PermissionPreset.ReadOnly);
        snapshot.Settings.Rules.Count.ShouldBe(1);
        snapshot.Settings.Rules[0].Decision.ShouldBe(RuleDecision.Deny);
        snapshot.Settings.Rules[0].Origin.ShouldBe("project");
    }

    [Fact]
    public void PreferencesFileOnlyAcceptsPersonalDisplayKeys()
    {
        _fx.Preferences("""{ "username": "dana", "agent": { "maxTurns": 3 } }""");

        var snapshot = _fx.Load();

        snapshot.Settings.Username.ShouldBe("dana");
        snapshot.Settings.Agent.MaxTurns.ShouldBe(20);
    }

    [Fact]
    public void ManagedWinsAndLocksBlockLowerTiers()
    {
        _fx.Managed_("""{ "lock": ["agent.maxTurns", "providers.*"], "agent": { "maxTurns": 6 } }""");
        _fx.User("""{ "agent": { "maxTurns": 12 }, "providers": { "mine": { "endpoint": "https://mine.example/" } } }""");

        var snapshot = _fx.Load(flags: new() { ["agent.maxTurns"] = "9" });

        snapshot.Settings.Agent.MaxTurns.ShouldBe(6);
        snapshot.Settings.Providers.ShouldNotContainKey("mine");
        snapshot.OriginOf("agent.maxTurns").Tier.ShouldBe(SettingsTier.Managed);
    }

    [Fact]
    public void SecretsInFilesAreIgnoredButEnvironmentIsAccepted()
    {
        _fx.User("""{ "providers": { "default": { "apiKey": "sk-should-not-load" } } }""");

        var fromFile = _fx.Load();
        fromFile.Settings.Providers["default"].ApiKey.ShouldBeNull();
        fromFile.Diagnostics.ShouldContain(d => d.Code == "secret_rejected");

        var fromEnv = _fx.Load(new Dictionary<string, string> { ["Providers__default__ApiKey"] = "sk-env" });
        fromEnv.Settings.Providers["default"].ApiKey.ShouldBe("sk-env");
        fromEnv.OriginOf("providers.default.apiKey").Effective!.Value.ShouldBe("(secret)");
        fromEnv.Redacted()["providers"]!["default"]!["apiKey"]!.GetValue<string>().ShouldBe("(secret)");
    }

    [Fact]
    public void InvalidValuesAreDroppedWithDiagnostics()
    {
        _fx.User("""{ "agent": { "maxTurns": 5000, "busyDelivery": "sometimes" }, "bogus": 1 }""");

        var snapshot = _fx.Load();

        snapshot.IsValid.ShouldBeFalse();
        snapshot.Settings.Agent.MaxTurns.ShouldBe(20);
        snapshot.Diagnostics.Select(d => d.Code).ShouldBe(["out_of_range", "invalid_value", "unknown_key"], ignoreOrder: true);
    }

    [Fact]
    public void LegacyKeysAreMigrated()
    {
        _fx.User("""
        {
          "Provider": { "Protocol": "Anthropic", "Endpoint": "https://api.anthropic.com/", "MaxOutputTokens": 4096 },
          "Mcp": { "Servers": { "files": { "Command": "node", "Arguments": ["server.js"] } } },
          "Agent": { "PermissionMode": "ReadOnly", "MaxContextCharacters": 60000 }
        }
        """);

        var snapshot = _fx.Load();

        snapshot.IsValid.ShouldBeTrue(string.Join("; ", snapshot.Diagnostics.Select(d => d.Message)));
        snapshot.Settings.Providers["default"].Protocol.ShouldBe(ProviderProtocol.Anthropic);
        snapshot.Settings.Providers["default"].MaxOutputTokens.ShouldBe(4096);
        snapshot.Settings.Connectors.Servers["files"].Command.ShouldBe("node");
        snapshot.Settings.Agent.PermissionPreset.ShouldBe(PermissionPreset.ReadOnly);
        snapshot.Settings.Context.MaxCharacters.ShouldBe(60000);
        snapshot.Diagnostics.Count(d => d.Code == "legacy_key_migrated").ShouldBe(4);
    }

    [Fact]
    public void SemanticChecksCatchInsecureEndpointsAndMissingCommands()
    {
        _fx.User("""
        {
          "providers": { "bad": { "endpoint": "http://remote.example/v1" }, "local": { "endpoint": "http://localhost:11434/v1/" }, "cred": { "endpoint": "https://user:pw@x.example/" } },
          "connectors": { "servers": { "nocmd": { "transport": "stdio" } } },
          "agent": { "model": "nosuch/gpt" }
        }
        """);

        var snapshot = _fx.Load();

        var codes = snapshot.Diagnostics.Select(d => d.Code).ToList();
        codes.ShouldContain("insecure_url");
        codes.ShouldContain("url_credentials");
        codes.ShouldContain("connector_command");
        codes.ShouldContain("unknown_provider");
        snapshot.Diagnostics.Count(d => d.Code == "insecure_url").ShouldBe(1);
    }

    [Fact]
    public void ParseErrorsInvalidateTheSnapshotWithoutThrowing()
    {
        _fx.User("{ not json");

        var snapshot = _fx.Load();

        snapshot.IsValid.ShouldBeFalse();
        snapshot.Diagnostics.ShouldContain(d => d.Code == "parse_error");
    }

    [Fact]
    public void JsoncCommentsAndTrailingCommasAreAccepted()
    {
        _fx.User("""
        {
          // keep it short
          "agent": { "maxTurns": 7, /* inline */ },
        }
        """);

        var snapshot = _fx.Load();

        snapshot.IsValid.ShouldBeTrue();
        snapshot.Settings.Agent.MaxTurns.ShouldBe(7);
    }

    [Fact]
    public void SnapshotIdChangesOnlyWhenEffectiveSettingsChange()
    {
        var first = _fx.Load();
        var same = _fx.Load();
        _fx.User("""{ "agent": { "maxTurns": 3 } }""");
        var changed = _fx.Load();

        same.Id.ShouldBe(first.Id);
        changed.Id.ShouldNotBe(first.Id);
    }

    [Fact]
    public void ModelRefsParseProviderModelAndVariant()
    {
        ModelRef.TryParse("openrouter/anthropic/claude-x@high", out var full).ShouldBeTrue();
        full.ShouldBe(new ModelRef("openrouter", "anthropic/claude-x", "high"));
        ModelRef.TryParse("gpt-4.1", out var bare).ShouldBeTrue();
        bare.Provider.ShouldBe("default");
        ModelRef.TryParse("x/", out _).ShouldBeFalse();
    }
}

