using System.Xml.Linq;
using System.Text.Json;
using Shouldly;

namespace CodeAssist.ArchitectureTests;

public sealed class LayeringTests
{
    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["CodeAssist.Contracts"] = [],
        ["CodeAssist.Configuration"] = ["CodeAssist.Contracts"],
        ["CodeAssist.Sessions"] = ["CodeAssist.Configuration", "CodeAssist.Contracts", "CodeAssist.Tools", "CodeAssist.Models", "CodeAssist.Extensions", "CodeAssist.Persistence"],
        ["CodeAssist.Models"] = ["CodeAssist.Tools", "CodeAssist.Workspaces"],
        ["CodeAssist.Tools"] = ["CodeAssist.Contracts", "CodeAssist.Persistence", "CodeAssist.Workspaces"],
        ["CodeAssist.Extensions"] = ["CodeAssist.Tools", "CodeAssist.Workspaces", "CodeAssist.Persistence"],
        ["CodeAssist.Mcp"] = ["CodeAssist.Tools", "CodeAssist.Workspaces"],
        ["CodeAssist.Workspaces"] = [],
        ["CodeAssist.Persistence"] = ["CodeAssist.Contracts", "CodeAssist.Workspaces"],
        ["CodeAssist.Security"] = ["CodeAssist.Tools", "CodeAssist.Persistence"],
        ["CodeAssist.Server"] = ["CodeAssist.Sessions", "CodeAssist.Configuration", "CodeAssist.Models", "CodeAssist.Tools", "CodeAssist.Extensions", "CodeAssist.Mcp", "CodeAssist.Workspaces", "CodeAssist.Persistence", "CodeAssist.Security", "CodeAssist.Contracts"],
        ["CodeAssist.Client"] = ["CodeAssist.Contracts"],
        ["CodeAssist.Tui"] = ["CodeAssist.Client", "CodeAssist.Configuration", "CodeAssist.Contracts"],
        ["CodeAssist.Cli"] = ["CodeAssist.Tui", "CodeAssist.Client", "CodeAssist.Configuration", "CodeAssist.Contracts"],
        ["CodeAssist.Testing"] = ["CodeAssist.Sessions", "CodeAssist.Configuration", "CodeAssist.Contracts"],
    };

    [Fact]
    public void EveryProjectUnderSrcIsDeclared()
    {
        var actual = Directory.GetDirectories(Path.Combine(RepoRoot(), "src"), "CodeAssist.*")
            .Where(directory => File.Exists(Path.Combine(directory, Path.GetFileName(directory) + ".csproj")))
            .Select(Path.GetFileName);

        actual.OrderBy(x => x).ShouldBe(Allowed.Keys.OrderBy(x => x));
    }

    [Fact]
    public void ProjectReferencesFollowTheLayering()
    {
        foreach (var (project, allowed) in Allowed)
        {
            var references = ProjectReferences(project);

            references.Except(allowed).ShouldBeEmpty($"{project} has a forbidden reference");
        }
    }

    [Fact]
    public void DeclaredGraphHasNoCycles()
    {
        var visiting = new HashSet<string>();
        var done = new HashSet<string>();

        void Visit(string project)
        {
            if (done.Contains(project)) return;
            visiting.Add(project).ShouldBeTrue($"cycle through {project}");
            foreach (var dependency in Allowed[project]) Visit(dependency);
            visiting.Remove(project);
            done.Add(project);
        }

        foreach (var project in Allowed.Keys) Visit(project);
    }

    [Fact]
    public void ShippedProjectsDoNotReferenceTesting()
    {
        foreach (var (project, allowed) in Allowed.Where(pair => pair.Key != "CodeAssist.Testing"))
            allowed.ShouldNotContain("CodeAssist.Testing", $"{project} must not reference the test tooling project");
    }

    [Fact]
    public void Should_BundleServerWithoutAssemblyReference_When_BuildingCli()
    {
        var path = Path.Combine(RepoRoot(), "src", "CodeAssist.Cli", "CodeAssist.Cli.csproj");
        var server = XDocument.Load(path).Descendants("ProjectReference")
            .Single(item => Path.GetFileNameWithoutExtension((string)item.Attribute("Include")!) == "CodeAssist.Server");
        ((string?)server.Attribute("ReferenceOutputAssembly")).ShouldBe("false");
        ProjectReferences("CodeAssist.Cli").ShouldNotContain("CodeAssist.Server");
        ProjectReferences("CodeAssist.Client").ShouldBe(["CodeAssist.Contracts"]);
    }

    [Theory]
    [InlineData("CodeAssist.Tools", "Abstractions/ToolContracts.cs", "CodeAssist.Tools")]
    [InlineData("CodeAssist.Extensions", "Skills/SkillContracts.cs", "CodeAssist.Extensions.Skills")]
    [InlineData("CodeAssist.Extensions", "Plugins/Abstractions/IPluginContent.cs", "CodeAssist.Extensions.Plugins")]
    [InlineData("CodeAssist.Mcp", "Abstractions/IMcpConnections.cs", "CodeAssist.Mcp")]
    [InlineData("CodeAssist.Models", "IModelCatalog.cs", "CodeAssist.Models")]
    [InlineData("CodeAssist.Persistence", "IJournalStore.cs", "CodeAssist.Persistence")]
    [InlineData("CodeAssist.Persistence", "Journal/Journal.cs", "CodeAssist.Persistence")]
    public void Should_KeepInternalContractsWithFeatureOwner_When_InspectingSource(string project, string relativePath, string expectedNamespace)
    {
        var path = Path.Combine(RepoRoot(), "src", project, relativePath);
        File.Exists(path).ShouldBeTrue($"{project} must own {relativePath}");
        File.ReadAllText(path).ShouldContain($"namespace {expectedNamespace};");
        ProjectReferences("CodeAssist.Contracts").ShouldBeEmpty();
        ProjectReferences(project).ShouldNotContain("CodeAssist.Sessions");
    }

    [Fact]
    public void Should_KeepNamedPackagesWithinClientBoundary_When_LoadingEditorWorkspaces()
    {
        var packages = new Dictionary<string, string[]>
        {
            ["client"] = ["codeassist-contracts"],
            ["editor"] = ["@codeassist/client", "@codeassist/editor-views"],
            ["editor-views"] = [],
        };
        foreach (var (name, dependencies) in packages)
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "vscode-extension", "packages", name, "package.json")));
            manifest.RootElement.GetProperty("name").GetString().ShouldBe("@codeassist/" + name);
            var actual = manifest.RootElement.TryGetProperty("dependencies", out var declared)
                ? declared.EnumerateObject().Select(item => item.Name).Order().ToArray() : [];
            actual.ShouldBe(dependencies.Order().ToArray(), $"{name} must not introduce another runtime or UI dependency");
        }
        using var shell = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "vscode-extension", "package.json")));
        shell.RootElement.GetProperty("main").GetString().ShouldBe("./dist/extension.js");
        shell.RootElement.GetProperty("dependencies").GetProperty("@codeassist/editor").GetString().ShouldNotBeNullOrWhiteSpace();
        shell.RootElement.GetProperty("workspaces").EnumerateArray().Select(item => item.GetString()).ShouldBe(["packages/*"]);
        File.Exists(Path.Combine(RepoRoot(), "vscode-extension", "tsconfig.base.json")).ShouldBeTrue();
        File.Exists(Path.Combine(RepoRoot(), "codeassist-contracts", "package.json")).ShouldBeTrue();
        foreach (var retired in new[] { "package.json", "package-lock.json", "tsconfig.base.json", "pnpm-workspace.yaml" })
            File.Exists(Path.Combine(RepoRoot(), retired)).ShouldBeFalse($"{retired} belongs outside the repository root");
    }

    private static string[] ProjectReferences(string project)
    {
        var path = Path.Combine(RepoRoot(), "src", project, project + ".csproj");
        return XDocument.Load(path).Descendants("ProjectReference")
            .Where(item => !string.Equals((string?)item.Attribute("ReferenceOutputAssembly"), "false", StringComparison.OrdinalIgnoreCase))
            .Select(e => Path.GetFileNameWithoutExtension((string)e.Attribute("Include")!))
            .ToArray();
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodeAssist.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("CodeAssist.slnx not found above the test output directory.");
    }
}
