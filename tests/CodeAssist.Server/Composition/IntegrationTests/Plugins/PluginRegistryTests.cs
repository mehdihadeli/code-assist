using System.Text.Json;
using CodeAssist.Extensions.Plugins;
using Shouldly;

namespace CodeAssist.Server.Composition.IntegrationTests.Plugins;

public sealed class PluginRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "code-assist-plugin-registry-" + Guid.NewGuid().ToString("N"));
    private string RegistryPath => Path.Combine(_root, "registry");

    [Fact]
    public async Task Should_RetainExactSource_When_RegistryUsesCustomFilename()
    {
        var package = Create("team.review");
        PluginRegistry.Publish(package, RegistryPath);
        var source = Path.Combine(RegistryPath, "pilot.json");
        File.Move(Path.Combine(RegistryPath, PluginRegistry.FileName), source);
        var registry = await PluginRegistry.OpenAsync(source, TestContext.Current.CancellationToken);
        registry.Source.ShouldBe(PluginRegistry.SourceKey(source));
        var settings = new CodeAssist.Configuration.CodeAssistSettings();
        settings.Policy.Plugins.LocalPackages = false;
        settings.Policy.Plugins.Sources = [source];
        var store = PluginComposition.CreateStore(settings, _root);
        store.Install(await registry.ResolveAsync(package.Manifest.Id, cancellationToken: TestContext.Current.CancellationToken), registry.Source);
        store.SetEnabled(package.Manifest.Id, true, package.Digest);
        store.LoadEnabled().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Should_RecheckPersistedSource_When_OrganizationRestrictsCachedPlugins()
    {
        var package = Create("team.review");
        PluginRegistry.Publish(package, RegistryPath);
        var registry = await PluginRegistry.OpenAsync(RegistryPath, TestContext.Current.CancellationToken);
        var settings = new CodeAssist.Configuration.CodeAssistSettings();
        var store = PluginComposition.CreateStore(settings, _root);
        store.Install(await registry.ResolveAsync(package.Manifest.Id, cancellationToken: TestContext.Current.CancellationToken), registry.Source);
        store.SetEnabled(package.Manifest.Id, true, package.Digest);
        settings.Policy.Plugins.LocalPackages = false;
        settings.Policy.Plugins.Sources = [RegistryPath];
        store.LoadEnabled().Single().Digest.ShouldBe(package.Digest);
        settings.Policy.Plugins.Sources = [];
        var reopened = PluginComposition.CreateStore(settings, _root);
        Should.Throw<InvalidOperationException>(() => reopened.LoadEnabled());
        reopened.Diagnose().Issues.Single().Code.ShouldBe("policy-restricted");
        Should.Throw<InvalidOperationException>(() => reopened.Install([package]));
        settings.Policy.Plugins.Sources = [RegistryPath];
        reopened.LoadEnabled().Single().Digest.ShouldBe(package.Digest);
    }

    [Fact]
    public async Task Should_RejectSourceBeforeAccess_When_UserAliasRebindsManagedSource()
    {
        var settings = new CodeAssist.Configuration.CodeAssistSettings();
        settings.PluginLibraries["team"] = new() { Source = "https://example.test/other.json" };
        settings.Policy.Plugins.Sources = ["https://example.test/reviewed.json"];
        await Should.ThrowAsync<InvalidOperationException>(() => PluginComposition.OpenRegistryAsync(settings, "team", _root, CancellationToken.None));
        settings.Policy.Plugins.Sources = ["team"];
        await Should.ThrowAsync<InvalidOperationException>(() => PluginComposition.OpenRegistryAsync(settings, "team", _root, CancellationToken.None));
        settings.Policy.Plugins.Sources = [];
        await Should.ThrowAsync<InvalidOperationException>(() => PluginComposition.OpenRegistryAsync(settings, Path.Combine(_root, "missing"), _root, CancellationToken.None));
    }

    [Fact]
    public async Task Should_SelectLaneAndRankLocalSignals_When_CatalogHasDiscoveryMetadata()
    {
        var plugin = Create("team.review");
        PluginRegistry.Publish(plugin, RegistryPath, lane: "pilot");
        var file = Path.Combine(RegistryPath, PluginRegistry.FileName);
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
        document["releases"]![0]!["fit"] = System.Text.Json.Nodes.JsonNode.Parse("""{"fileTypes":[".cs"],"folders":["services"],"commands":["dotnet"],"dependencies":["Example.Sdk"]}""");
        File.WriteAllText(file, document.ToJsonString());
        var registry = await PluginRegistry.OpenAsync(RegistryPath, TestContext.Current.CancellationToken);
        registry.Search().ShouldBeEmpty();
        registry.Search("shared", "pilot").Single().Id.ShouldBe(plugin.Manifest.Id);
        (await registry.ResolveAsync(plugin.Manifest.Id, cancellationToken: TestContext.Current.CancellationToken, lane: "pilot")).Single().Digest.ShouldBe(plugin.Digest);
        await Should.ThrowAsync<KeyNotFoundException>(() => registry.ResolveAsync(plugin.Manifest.Id));
        var activity = new PluginActivity(["src\\Main.CS"], ["services/api"], ["dotnet"], ["Example.Sdk"]);
        PluginRecommendations.Rank(registry, activity, [], "pilot").Single().Reasons.ShouldBe(["file-type", "folder", "command", "dependency"]);
        PluginRecommendations.Rank(registry, activity, [plugin.Manifest.Id], "pilot").ShouldBeEmpty();
        registry.Document.Releases.Single().Fit.FileTypes.Clear();
        registry.Search(lane: "pilot").Single().Fit.FileTypes.ShouldBe([".cs"]);
        registry.Search(lane: "pilot").Single().Fit.Folders.Clear();
        PluginRecommendations.Rank(registry, activity, [], "pilot").Single().Reasons.Count.ShouldBe(4);
        var unrelated = new PluginActivity([], ["services-other"], ["dotnet test"], []);
        PluginRecommendations.Rank(registry, unrelated, [], "pilot").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("{\"fileTypes\":[\"**/*.cs\"]}")]
    [InlineData("{\"folders\":[\"../outside\"]}")]
    [InlineData("{\"commands\":[\"dotnet test\"]}")]
    [InlineData("{\"dependencies\":null}")]
    public async Task Should_RejectInvalidFitMetadata_When_RegistryIsOpened(string fit)
    {
        PluginRegistry.Publish(Create("team.review"), RegistryPath);
        var file = Path.Combine(RegistryPath, PluginRegistry.FileName);
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
        document["releases"]![0]!["fit"] = System.Text.Json.Nodes.JsonNode.Parse(fit);
        File.WriteAllText(file, document.ToJsonString());
        await Should.ThrowAsync<InvalidDataException>(() => PluginRegistry.OpenAsync(RegistryPath));
    }

    [Fact]
    public async Task Should_ResolveVerifiedDependencyClosure_When_InstallingPublishedPlugin()
    {
        var dependency = Create("team.base");
        var plugin = Create("team.review", new() { ["team.base"] = "1.0.0" });
        PluginRegistry.Publish(dependency, RegistryPath, "team.library");
        PluginRegistry.Publish(plugin, RegistryPath);
        var registry = await PluginRegistry.OpenAsync(RegistryPath, TestContext.Current.CancellationToken);
        registry.Document.Name.ShouldBe("team.library");
        registry.Document.Releases.Clear();
        registry.Document.Releases.Count.ShouldBe(2);
        var resolved = await registry.ResolveAsync(plugin.Manifest.Id, cancellationToken: TestContext.Current.CancellationToken);
        resolved.Select(item => item.Manifest.Id).ShouldBe(["team.base", "team.review"]);
        var store = new PluginStore(Path.Combine(_root, "store"));
        store.Install(resolved);
        store.List().Count.ShouldBe(2);
        store.List().ShouldAllBe(item => !item.Enabled);
        Should.Throw<InvalidOperationException>(() => PluginRegistry.Publish(plugin, RegistryPath));
    }

    [Fact]
    public async Task Should_RejectTampering_When_ArchiveChecksumChanges()
    {
        var plugin = Create("team.review");
        PluginRegistry.Publish(plugin, RegistryPath);
        var registry = await PluginRegistry.OpenAsync(RegistryPath, TestContext.Current.CancellationToken);
        var archive = Path.Combine(RegistryPath, registry.Document.Releases.Single().Archive);
        File.AppendAllText(archive, "tampered");
        await Should.ThrowAsync<InvalidDataException>(() => registry.ResolveAsync(plugin.Manifest.Id));
    }

    [Fact]
    public async Task Should_LeaveInstallationUntouched_When_RegistryDependencyIsMissing()
    {
        var plugin = Create("team.review", new() { ["team.missing"] = "1.0.0" });
        PluginRegistry.Publish(plugin, RegistryPath);
        var registry = await PluginRegistry.OpenAsync(RegistryPath, TestContext.Current.CancellationToken);
        await Should.ThrowAsync<KeyNotFoundException>(() => registry.ResolveAsync(plugin.Manifest.Id));
        new PluginStore(Path.Combine(_root, "store")).List().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("http://example.test/assist-registry.json")]
    [InlineData("https://user:password@example.test/assist-registry.json")]
    [InlineData("file:///tmp/assist-registry.json")]
    [InlineData("https://example.test/assist-registry.json#fragment")]
    public async Task Should_RejectUnsafeRegistryOrigins_When_SourceIsRemote(string source) =>
        await Should.ThrowAsync<InvalidDataException>(() => PluginRegistry.OpenAsync(source));

    private PluginPackage Create(string id, Dictionary<string, string>? requires = null)
    {
        var directory = Path.Combine(_root, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, PluginManifest.FileName), JsonSerializer.Serialize(new PluginManifest
        {
            Id = id, Release = "1.0.0", Summary = "Shared guidance", Requires = requires ?? [],
            Contributions = [new() { Key = "inspect", Kind = "prompt", Entry = "inspect.md", Description = "Inspect" }],
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        File.WriteAllText(Path.Combine(directory, "inspect.md"), "Inspect the requested changes and report concrete findings.");
        return PluginPackage.Open(directory);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}