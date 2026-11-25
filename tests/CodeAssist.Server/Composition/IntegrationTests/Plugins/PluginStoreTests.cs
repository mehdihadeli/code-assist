using System.IO.Compression;
using System.Text.Json;
using CodeAssist.Extensions.Plugins;
using Shouldly;

namespace CodeAssist.Server.Composition.IntegrationTests.Plugins;

public sealed class PluginStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "code-assist-plugin-store-" + Guid.NewGuid().ToString("N"));
    private PluginStore Store => new(Path.Combine(_root, "store"));

    [Theory]
    [InlineData("")]
    [InlineData("\u0000")]
    [InlineData("http://plugins.example.com/catalog.json")]
    public void Should_ReportStateCorruptionWithoutMutation_When_RecordedSourceIsInvalid(string source)
    {
        Store.Install([Create("team.review")]);
        var path = Path.Combine(Store.DirectoryPath, "installed.json");
        var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        state["installed"]![0]!["source"] = source;
        var bytes = state.ToJsonString();
        File.WriteAllText(path, bytes);
        var report = Store.Diagnose();
        report.Ready.ShouldBeFalse();
        report.Issues.Single().Code.ShouldBe("state-unreadable");
        File.ReadAllText(path).ShouldBe(bytes);
    }

    [Fact]
    public void Should_ExplainTransitiveFailureWithoutChangingState_When_DependencyContentIsDamaged()
    {
        var dependency = Create("team.base");
        var middle = Create("team.middle", requires: new() { [dependency.Manifest.Id] = "1.0.0" });
        var root = Create("team.review", requires: new() { [middle.Manifest.Id] = "1.0.0" });
        Store.Install([dependency, middle, root]);
        foreach (var package in new[] { dependency, middle, root }) Store.SetEnabled(package.Manifest.Id, true, package.Digest);
        var statePath = Path.Combine(Store.DirectoryPath, "installed.json");
        var before = File.ReadAllText(statePath);
        File.Delete(Path.Combine(Store.DirectoryPath, "objects", dependency.Digest, "inspect.md"));
        var report = Store.Diagnose();
        report.Ready.ShouldBeFalse();
        report.Issues.Single(issue => issue.Id == dependency.Manifest.Id).Code.ShouldBe("content-unreadable");
        report.Issues.Count(issue => issue.Code == "dependency-unavailable").ShouldBe(2);
        File.ReadAllText(statePath).ShouldBe(before);
        File.WriteAllText(statePath, "{broken");
        Store.Diagnose().Issues.Single().Code.ShouldBe("state-unreadable");
        File.ReadAllText(statePath).ShouldBe("{broken");
    }

    [Fact]
    public void Should_EnforceRequiredDigestAcrossWorkspaces_When_AdministratorPinsContent()
    {
        var package = Create("team.review");
        Store.Install([package]);
        Store.SetEnabled(package.Manifest.Id, false, workspace: _root);
        var guarded = new PluginStore(Store.DirectoryPath, requiredDigests: new Dictionary<string, string> { [package.Manifest.Id] = package.Digest });
        guarded.LoadEnabled(_root).Single().Digest.ShouldBe(package.Digest);
        var settings = new CodeAssist.Configuration.CodeAssistSettings();
        settings.Policy.Plugins.Required[package.Manifest.Id] = package.Digest;
        settings.Plugins.Add(new() { Package = package.Manifest.Id, Enabled = false });
        PluginComposition.Permitted(settings, package.Manifest.Id).ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => guarded.SetEnabled(package.Manifest.Id, false, workspace: _root));
        Should.Throw<InvalidOperationException>(() => guarded.Remove(package.Manifest.Id));
        Should.Throw<InvalidOperationException>(() => guarded.Install([Create("team.review", release: "2.0.0")]));
        Store.List().Single().Enabled.ShouldBeFalse();
        var missing = new PluginStore(Store.DirectoryPath, requiredDigests: new Dictionary<string, string> { ["team.missing"] = package.Digest });
        Should.Throw<InvalidOperationException>(() => missing.LoadEnabled());
    }

    [Fact]
    public void Should_ApplyOrganizationAdmission_When_CatalogUsesSharedComposition()
    {
        var package = Create("team.review");
        var settings = new CodeAssist.Configuration.CodeAssistSettings();
        var store = PluginComposition.CreateStore(settings, _root);
        store.Install([package]);
        store.SetEnabled(package.Manifest.Id, true, package.Digest);
        settings.Policy.Plugins.Ids = [];
        Should.Throw<InvalidOperationException>(() => PluginComposition.CreateCatalog(settings, _root, _root).Entries);
        settings.Policy.Plugins.Ids = [package.Manifest.Id];
        PluginComposition.CreateCatalog(settings, _root, _root).Entries.Count.ShouldBe(1);
        settings.Policy.Plugins.Reject.Add(package.Manifest.Id);
        Should.Throw<InvalidOperationException>(() => store.LoadEnabled());
    }

    [Fact]
    public void Should_RejectEntireInstall_When_DependencyIsRestricted()
    {
        var dependency = Create("team.base");
        var package = Create("team.review", requires: new() { ["team.base"] = "1.0.0" });
        var guarded = new PluginStore(Store.DirectoryPath, candidate => candidate.Manifest.Id == "team.base" ? "Denied by administrator." : null);
        Should.Throw<InvalidOperationException>(() => guarded.Install([package, dependency]));
        Store.List().ShouldBeEmpty();
    }

    [Fact]
    public void Should_RecheckPolicyWithoutRewritingApproval_When_PreviouslyApprovedPluginIsRestricted()
    {
        var package = Create("team.review");
        Store.Install([package]);
        Store.SetEnabled(package.Manifest.Id, true, package.Digest);
        var guarded = new PluginStore(Store.DirectoryPath, _ => "Digest no longer permitted.");
        Should.Throw<InvalidOperationException>(() => guarded.LoadEnabled());
        Should.Throw<InvalidOperationException>(() => guarded.SetEnabled(package.Manifest.Id, true, package.Digest, _root));
        Store.LoadEnabled().Single().Digest.ShouldBe(package.Digest);
        guarded.SetEnabled(package.Manifest.Id, false);
        Store.LoadEnabled().ShouldBeEmpty();
    }

    [Fact]
    public void Should_RoundTripDeclaredFilesOnly_When_PackageIsPublished()
    {
        var package = Create("team.review");
        File.WriteAllText(Path.Combine(_root, "team.review", ".env"), "not packaged");
        var archive = Path.Combine(_root, "release.zip");
        package.Pack(archive);
        var restored = PluginPackage.FromArchive(archive);
        restored.Digest.ShouldBe(package.Digest);
        restored.Text("inspect.md").ShouldBe("Review the diff.");
        using var zip = ZipFile.OpenRead(archive);
        zip.Entries.Count.ShouldBe(2);
        Should.Throw<IOException>(() => package.Pack(archive));
    }

    [Fact]
    public void Should_PreserveReviewedManifest_When_CallerMutatesReturnedCollections()
    {
        var package = Create("team.review");
        var manifest = package.Manifest;
        manifest.Requires["team.unknown"] = "1.0.0";
        manifest.Assets.Add("undeclared.md");
        manifest.Contributions[0].Tools.Add("process.run");
        manifest.Contributions.Clear();
        package.Manifest.Requires.ShouldBeEmpty();
        package.Manifest.Assets.ShouldBeEmpty();
        package.Manifest.Contributions.Single().Tools.ShouldBe(["fs.read"]);
        Store.Install([package]);
        Store.SetEnabled(package.Manifest.Id, true, package.Digest);
        Store.LoadEnabled().Single().Manifest.Contributions.Count.ShouldBe(1);
    }

    [Fact]
    public void Should_LeaveStateUnchanged_When_InstalledContentExceedsGraphBudget()
    {
        var packages = new List<PluginPackage>();
        var bytes = new byte[1_048_576];
        for (var index = 0; index < 5; index++)
        {
            var id = "team.large-" + index;
            var directory = Path.Combine(_root, id);
            Directory.CreateDirectory(directory);
            var assets = Enumerable.Range(0, 13).Select(number => "asset-" + number + ".bin").ToList();
            File.WriteAllText(Path.Combine(directory, PluginManifest.FileName), JsonSerializer.Serialize(new PluginManifest
            {
                Id = id, Release = "1.0.0", Summary = "Large data", Assets = assets,
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            foreach (var asset in assets) File.WriteAllBytes(Path.Combine(directory, asset), bytes);
            packages.Add(PluginPackage.Open(directory));
        }
        Should.Throw<InvalidDataException>(() => Store.Install(packages));
        Store.List().ShouldBeEmpty();
    }

    [Fact]
    public void Should_InstallDisabledAndRequireDigest_When_EnablingPlugin()
    {
        var package = Create("team.review");
        Store.Install([package]);
        new PluginStore(Store.DirectoryPath).List().Single().Enabled.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => Store.SetEnabled("team.review", true, "wrong"));
        Store.SetEnabled("team.review", true, package.Digest);
        Store.LoadEnabled().Single().Digest.ShouldBe(package.Digest);
    }

    [Fact]
    public void Should_KeepWorkspaceDecisionsLocal_When_UserPluginIsDisabledForOneRoot()
    {
        var package = Create("team.review");
        Store.Install([package]);
        Store.SetEnabled(package.Manifest.Id, true, package.Digest);
        Store.SetEnabled(package.Manifest.Id, false, workspace: Path.Combine(_root, "workspace"));
        Store.LoadEnabled(Path.Combine(_root, "workspace")).ShouldBeEmpty();
        Store.LoadEnabled(Path.Combine(_root, "other")).Count.ShouldBe(1);
        Store.LoadEnabled().Count.ShouldBe(1);
    }

    [Fact]
    public void Should_ValidateRequirementsAtomically_When_InstallingBatch()
    {
        var dependency = Create("team.base");
        var root = Create("team.review", requires: new() { ["team.base"] = "1.0.0" });
        Should.Throw<InvalidOperationException>(() => Store.Install([root]));
        Store.List().ShouldBeEmpty();
        Store.Install([root, dependency]);
        Should.Throw<InvalidOperationException>(() => Store.SetEnabled(root.Manifest.Id, true, root.Digest));
        Store.List().ShouldAllBe(item => !item.Enabled);
        Store.SetEnabled(dependency.Manifest.Id, true, dependency.Digest);
        Store.SetEnabled(root.Manifest.Id, true, root.Digest);
        Store.LoadEnabled().Select(package => package.Manifest.Id).ShouldBe(["team.base", "team.review"]);
        Should.Throw<InvalidOperationException>(() => Store.SetEnabled(dependency.Manifest.Id, false));
        Should.Throw<InvalidOperationException>(() => Store.Remove(dependency.Manifest.Id));
        Store.LoadEnabled().Count.ShouldBe(2);
    }

    [Fact]
    public void Should_RejectCyclesAndConflicts_When_ResolvingDependencies()
    {
        var first = Create("team.first", requires: new() { ["team.second"] = "1.0.0" });
        var second = Create("team.second", requires: new() { ["team.first"] = "1.0.0" });
        Should.Throw<InvalidDataException>(() => Store.Install([first, second]));
        Store.List().ShouldBeEmpty();
        var wrong = Create("team.second", release: "2.0.0");
        Should.Throw<InvalidOperationException>(() => Store.Install([first, wrong]));
        Store.List().ShouldBeEmpty();
    }

    [Fact]
    public void Should_RevokeActivationOnUpdate_When_ReleaseChanges()
    {
        var first = Create("team.review");
        Store.Install([first]);
        Store.SetEnabled(first.Manifest.Id, true, first.Digest);
        Store.SetEnabled(first.Manifest.Id, true, first.Digest, _root);
        var second = Create("team.review", release: "1.1.0");
        Store.Install([second]);
        Store.LoadEnabled().ShouldBeEmpty();
        Store.LoadEnabled(_root).ShouldBeEmpty();
        Should.Throw<InvalidOperationException>(() => Store.SetEnabled(second.Manifest.Id, true, first.Digest));
    }

    [Fact]
    public void Should_RejectModifiedReleaseAndCache_When_IdentityNoLongerMatches()
    {
        var package = Create("team.review");
        Store.Install([package]);
        var changed = Create("team.review", body: "Different guidance");
        Should.Throw<InvalidDataException>(() => Store.Install([changed]));
        Store.List().Single().Digest.ShouldBe(package.Digest);
        File.WriteAllText(Path.Combine(Store.DirectoryPath, "objects", package.Digest, "inspect.md"), "tampered");
        Should.Throw<InvalidDataException>(() => Store.SetEnabled(package.Manifest.Id, true, package.Digest));
    }

    [Fact]
    public void Should_StoreOnlyInvocationMetadata_When_RecordingUse()
    {
        var package = Create("team.review");
        Store.Install([package]);
        Store.RecordUse(package.Manifest.Id, "inspect", package.Digest);
        Store.RecordUse(package.Manifest.Id, "inspect", package.Digest);
        Store.Usage()["team.review/inspect"].Invocations.ShouldBe(2);
        Should.Throw<InvalidOperationException>(() => Store.RecordUse(package.Manifest.Id, "missing", package.Digest));
        Store.Remove(package.Manifest.Id);
        Store.List().ShouldBeEmpty();
        Store.Usage().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("../outside.md", 0)]
    [InlineData("escape.md", 0xA000)]
    [InlineData("unexpected.md", 0)]
    public void Should_RejectUnsafeArchives_When_EntriesAreUndeclaredOrLinks(string name, int mode)
    {
        Directory.CreateDirectory(_root);
        var archive = Path.Combine(_root, "bad.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry(name);
            entry.ExternalAttributes = mode << 16;
            using var output = new StreamWriter(entry.Open());
            output.Write("content");
        }
        Should.Throw<InvalidDataException>(() => PluginPackage.FromArchive(archive));
        File.Exists(Path.Combine(_root, "outside.md")).ShouldBeFalse();
    }

    [Fact]
    public void Should_FailClosed_When_StateIsCorrupt()
    {
        var package = Create("team.review");
        Store.Install([package]);
        File.WriteAllText(Path.Combine(Store.DirectoryPath, "installed.json"), "{broken");
        Should.Throw<InvalidDataException>(() => Store.LoadEnabled());
    }

    private PluginPackage Create(string id, string release = "1.0.0", Dictionary<string, string>? requires = null, string body = "Review the diff.")
    {
        var directory = Path.Combine(_root, id);
        Directory.CreateDirectory(directory);
        var manifest = new PluginManifest { Id = id, Release = release, Summary = "Review guidance", Requires = requires ?? [], Contributions =
            [new() { Key = "inspect", Kind = "skill", Entry = "inspect.md", Description = "Inspect changes", Tools = ["fs.read"] }] };
        File.WriteAllText(Path.Combine(directory, PluginManifest.FileName), JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        File.WriteAllText(Path.Combine(directory, "inspect.md"), body);
        return PluginPackage.Open(directory);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}