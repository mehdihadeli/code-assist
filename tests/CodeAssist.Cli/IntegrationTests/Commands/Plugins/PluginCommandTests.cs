using System.Text.Json.Nodes;
using CodeAssist.Cli.Hosting;
using CodeAssist.Cli.IntegrationTests.Support;
using Shouldly;

namespace CodeAssist.Cli.IntegrationTests.Commands.Plugins;

public sealed class PluginCommandTests : IDisposable
{
    private readonly CliHarness _cli = new();
    private string Source => Path.Combine(_cli.Files.Locations.WorkingDirectory, "review-plugin");

    [Fact]
    public async Task Should_RequireReviewedContentAndExplainDamage_When_ManagedPolicyOwnsActivation()
    {
        (await _cli.Run("plugins", "init", Source, "--id", "team.review")).Code.ShouldBe(0);
        var installed = await _cli.Run("plugins", "install", Source, "--json");
        installed.Code.ShouldBe(0, installed.Output);
        var digest = JsonNode.Parse(installed.Output)!.AsArray().Single()!["digest"]!.GetValue<string>();
        var managed = Path.Combine(_cli.Files.Managed, "settings.json");
        File.WriteAllText(managed, new JsonObject
        {
            ["policy"] = new JsonObject { ["plugins"] = new JsonObject { ["required"] = new JsonObject { ["team.review"] = digest } } },
        }.ToJsonString());
        (await _cli.Run("plugins", "expand", "team.review/inspect")).Code.ShouldBe(0);
        (await _cli.Run("plugins", "disable", "team.review", "--workspace")).Code.ShouldBe(ExitCodes.UsageOrConfig);
        (await _cli.Run("plugins", "remove", "team.review")).Code.ShouldBe(ExitCodes.UsageOrConfig);
        var objectRoot = Path.Combine(_cli.Files.Locations.StateDirectory, "plugins", "objects", digest);
        Directory.Delete(objectRoot, recursive: true);
        var report = await _cli.Run("plugins", "diagnose", "--json");
        report.Code.ShouldBe(ExitCodes.UsageOrConfig, report.Output);
        JsonNode.Parse(report.Output)!["issues"]!.AsArray().Single()!["code"]!.GetValue<string>().ShouldBe("content-unreadable");
        File.Exists(_cli.UserFile).ShouldBeFalse();
    }

    [Fact]
    public async Task Should_DiscoverNamedLaneWithoutInstalling_When_SearchingAndSuggesting()
    {
        (await _cli.Run("plugins", "init", Source, "--id", "team.review")).Code.ShouldBe(0);
        var registry = Path.Combine(_cli.Files.Locations.WorkingDirectory, "library");
        (await _cli.Run("plugins", "publish", Source, "--registry", registry, "--lane", "pilot")).Code.ShouldBe(0);
        var catalogPath = Path.Combine(registry, "assist-registry.json");
        var document = JsonNode.Parse(File.ReadAllText(catalogPath))!;
        document["releases"]![0]!["fit"] = JsonNode.Parse("""{"fileTypes":[".cs"],"commands":["dotnet"]}""");
        File.WriteAllText(catalogPath, document.ToJsonString());
        _cli.Environment["PluginLibraries__team__Source"] = registry;
        (await _cli.Run("plugins", "libraries", "--json")).Output.ShouldContain("team");
        (await _cli.Run("plugins", "validate", registry, "--json")).Code.ShouldBe(0);
        (await _cli.Run("plugins", "search", "team", "--json")).Output.Trim().ShouldBe("[]");
        var search = await _cli.Run("plugins", "search", "team", "--lane", "pilot", "--query", "review", "--json");
        search.Code.ShouldBe(0, search.Output);
        JsonNode.Parse(search.Output)!.AsArray().Single()!["id"]!.GetValue<string>().ShouldBe("team.review");
        var suggestion = await _cli.Run("plugins", "suggest", "team", "--lane", "pilot", "--seen-file", "not-read.cs", "--seen-command", "dotnet", "--json");
        suggestion.Code.ShouldBe(0, suggestion.Output);
        JsonNode.Parse(suggestion.Output)!.AsArray().Single()!["reasons"]!.AsArray().Count.ShouldBe(2);
        (await _cli.Run("plugins", "list", "--json")).Output.Trim().ShouldBe("[]");
        (await _cli.Run("plugins", "install", "team", "--id", "team.review", "--lane", "pilot")).Code.ShouldBe(0);
        var diagnose = await _cli.Run("plugins", "diagnose", "--json");
        diagnose.Code.ShouldBe(0, diagnose.Output);
        JsonNode.Parse(diagnose.Output)!["ready"]!.GetValue<bool>().ShouldBeTrue();
        File.Exists(_cli.UserFile).ShouldBeFalse();
    }

    [Fact]
    public async Task Should_CreateInstallApproveExpandAndRemove_When_PluginLifecycleIsExplicit()
    {
        var created = await _cli.Run("plugins", "init", Source, "--id", "team.review", "--json");
        created.Code.ShouldBe(0, created.Output);
        (await _cli.Run("plugins", "validate", Source)).Code.ShouldBe(0);
        var installed = await _cli.Run("plugins", "install", Source, "--json");
        installed.Code.ShouldBe(0, installed.Output);
        var item = JsonNode.Parse(installed.Output)!.AsArray().Single()!;
        item["enabled"]!.GetValue<bool>().ShouldBeFalse();
        var digest = item["digest"]!.GetValue<string>();
        (await _cli.Run("plugins", "enable", "team.review")).Code.ShouldBe(ExitCodes.UsageOrConfig);
        var enabled = await _cli.Run("plugins", "enable", "team.review", "--approve", digest, "--json");
        enabled.Code.ShouldBe(0, enabled.Output);
        var expanded = await _cli.Run("plugins", "expand", "team.review/inspect", "--input", "Check this diff", "--json");
        expanded.Code.ShouldBe(0, expanded.Output);
        JsonNode.Parse(expanded.Output)!["text"]!.GetValue<string>().ShouldContain("Check this diff");
        var stats = await _cli.Run("plugins", "stats", "--json");
        JsonNode.Parse(stats.Output)!["team.review/inspect"]!["invocations"]!.GetValue<int>().ShouldBe(1);
        (await _cli.Run("plugins", "disable", "team.review", "--workspace")).Code.ShouldBe(0);
        (await _cli.Run("plugins", "expand", "team.review/inspect")).Code.ShouldBe(ExitCodes.UsageOrConfig);
        (await _cli.Run("plugins", "remove", "team.review")).Code.ShouldBe(0);
        (await _cli.Run("plugins", "list", "--json")).Output.Trim().ShouldBe("[]");
        File.Exists(_cli.UserFile).ShouldBeFalse();
    }

    [Fact]
    public async Task Should_PublishAndInstallRegistryRelease_When_SourceIsExplicit()
    {
        (await _cli.Run("plugins", "init", Source, "--id", "team.review")).Code.ShouldBe(0);
        var registry = Path.Combine(_cli.Files.Locations.WorkingDirectory, "library");
        var published = await _cli.Run("plugins", "publish", Source, "--registry", registry, "--name", "team.library");
        published.Code.ShouldBe(0, published.Output);
        var installed = await _cli.Run("plugins", "install", registry, "--id", "team.review", "--release", "0.1.0", "--json");
        installed.Code.ShouldBe(0, installed.Output);
        JsonNode.Parse(installed.Output)!.AsArray().Single()!["release"]!.GetValue<string>().ShouldBe("0.1.0");
    }

    [Fact]
    public async Task Should_RefuseOverwriteAndPolicyDeniedEnable_When_PluginIsManaged()
    {
        (await _cli.Run("plugins", "init", Source, "--id", "team.review")).Code.ShouldBe(0);
        (await _cli.Run("plugins", "init", Source, "--id", "team.review")).Code.ShouldBe(ExitCodes.UsageOrConfig);
        var installed = await _cli.Run("plugins", "install", Source, "--json");
        var digest = JsonNode.Parse(installed.Output)!.AsArray().Single()!["digest"]!.GetValue<string>();
        _cli.Environment["Plugins__0__Package"] = "team.review";
        _cli.Environment["Plugins__0__Enabled"] = "false";
        var result = await _cli.Run("plugins", "enable", "team.review", "--approve", digest);
        result.Code.ShouldBe(ExitCodes.UsageOrConfig);
        result.Output.ShouldContain("restricted");
        File.Exists(_cli.UserFile).ShouldBeFalse();
    }

    public void Dispose() => _cli.Dispose();
}