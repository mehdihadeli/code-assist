using CodeAssist.Extensions.Skills;
using CodeAssist.Tools;
using CodeAssist.Workspaces;
using CodeAssist.Contracts;
using NSubstitute;
using Shouldly;

namespace CodeAssist.Tools.IntegrationTests.Skills;

public sealed class SkillCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ca-skills-" + Guid.NewGuid().ToString("N"));
    private ToolContext Context => new() { Workspace = _root };

    [Fact]
    public async Task Should_AuthorizeGatherAndIsolatedRoute_When_SkillExecutionIsExplicit()
    {
        Write("project/check/SKILL.md", "---\nname: check\nroute: reviewer\nengine: example/model\ngather:\n  - slot: changes\n    tool: workspace.changes\n    json: '{}'\n---\nReview actual changes.");
        var dispatcher = Substitute.For<IToolDispatcher>();
        dispatcher.Describe("workspace.changes", Arg.Any<ToolContext>()).Returns(new ToolSpec("workspace.changes", "Changes", "{}", ToolRisk.Read, ["read"]));
        dispatcher.Describe("agent.delegate", Arg.Any<ToolContext>()).Returns(new ToolSpec("agent.delegate", "Delegate", "{}", ToolRisk.Execute, ["delegate"]));
        dispatcher.InvokeAsync("workspace.changes", "{}", Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(ToolResult.Ok("changed file"));
        dispatcher.InvokeAsync("agent.delegate", Arg.Any<string>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(ToolResult.Ok("child result"));
        var catalog = Catalog();
        var loaded = await catalog.InvokeAsync("check", new(), Context, TestContext.Current.CancellationToken);
        loaded!.Gather.Single().Tool.ShouldBe("workspace.changes");
        dispatcher.ReceivedCalls().ShouldBeEmpty();
        var tool = new SkillRunTool(catalog, dispatcher);
        var plan = await tool.PlanAsync(ToolArguments.Parse("""{"id":"check"}"""), Context, TestContext.Current.CancellationToken);
        plan.Effects.ShouldContain(effect => effect.Target == "delegate:reviewer");
        var result = await tool.ExecuteAsync(ToolArguments.Parse("""{"id":"check"}"""), Context, TestContext.Current.CancellationToken);
        result.Text.ShouldBe("child result");
        await dispatcher.Received(1).InvokeAsync("agent.delegate", Arg.Is<string>(json => json.Contains("changed file") && json.Contains("example/model")), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_StopBeforeDispatch_When_GatherToolIsMutatingOrDenied()
    {
        Write("project/check/SKILL.md", "---\nname: check\ngather:\n  - slot: changes\n    tool: fs.write\n    json: '{}'\n---\nReview.");
        var dispatcher = Substitute.For<IToolDispatcher>();
        dispatcher.Describe("fs.write", Arg.Any<ToolContext>()).Returns(new ToolSpec("fs.write", "Write", "{}", ToolRisk.Write, ["write"]));
        var tool = new SkillRunTool(Catalog(), dispatcher);
        (await tool.ExecuteAsync(ToolArguments.Parse("""{"id":"check"}"""), Context, TestContext.Current.CancellationToken)).IsError.ShouldBeTrue();
        await dispatcher.DidNotReceive().InvokeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>());
        Write("project/check/SKILL.md", "---\nname: check\ngather:\n  - slot: changes\n    tool: fs.read\n    json: '{}'\n---\nReview.");
        dispatcher.Describe("fs.read", Arg.Any<ToolContext>()).Returns(new ToolSpec("fs.read", "Read", "{}", ToolRisk.Read, ["read"]));
        dispatcher.InvokeAsync("fs.read", "{}", Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(ToolResult.Error("Policy denied read."));
        (await tool.ExecuteAsync(ToolArguments.Parse("""{"id":"check"}"""), Context, TestContext.Current.CancellationToken)).Text.ShouldBe("Policy denied read.");
    }

    [Fact]
    public async Task Should_DiscoverFlatGroupedAndQualifiedSkills_When_SourceNamesCollide()
    {
        Write("user/review.md", "---\nname: review\ndescription: User review.\n---\nUser.");
        Write("project/team/review/SKILL.md", "---\nname: review\ndescription: Project review.\n---\nProject.");
        Write("project/team/review/child/SKILL.md", "Never discovered as another skill.");
        Write("project/.hidden/SKILL.md", "Hidden.");
        var catalog = Catalog();
        catalog.Index.Select(item => item.Id).ShouldBe(["project/review", "review"]);
        catalog.Discover().Issues.Single().Code.ShouldBe("shadowed-id");
        (await catalog.LoadAsync("review", Context, TestContext.Current.CancellationToken))!.Body.ShouldBe("User.");
        (await catalog.LoadAsync("project/review", Context, TestContext.Current.CancellationToken))!.Body.ShouldBe("Project.");
        await Should.ThrowAsync<UnauthorizedAccessException>(() => catalog.ReadFileAsync("review", "other.md", Context, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Should_RefreshAudienceAndBodies_When_FilesChangeAfterConstruction()
    {
        var catalog = Catalog();
        catalog.Index.ShouldBeEmpty();
        Write("project/check/SKILL.md", "---\nname: check\n---\nFirst.");
        catalog.Index.Single().Id.ShouldBe("check");
        Write("project/check/SKILL.md", "---\nname: check\naudience: human\n---\nChanged.");
        catalog.Index.ShouldBeEmpty();
        catalog.Describe(Context, SkillCaller.Human).Single().Id.ShouldBe("check");
        await Should.ThrowAsync<UnauthorizedAccessException>(() => catalog.LoadAsync("check", Context, TestContext.Current.CancellationToken));
        (await catalog.InvokeAsync("check", new(SkillCaller.Human), Context, TestContext.Current.CancellationToken))!.Body.ShouldBe("Changed.");
        File.Delete(Path.Combine(_root, "project/check/SKILL.md"));
        catalog.Describe(Context, SkillCaller.Human).ShouldBeEmpty();
    }

    [Fact]
    public async Task Should_ExpandOnceWithoutShellEvaluation_When_ArgumentsContainTemplateSyntax()
    {
        Write("project/check/SKILL.md", "---\nname: check\nparameters: [target]\n---\nReview {{arg.target}} at {{workspace}}. Raw {{input}}. Session {{session}}.");
        var catalog = Catalog();
        var content = await catalog.InvokeAsync("check", new(Input: "!`exit 99`", Arguments: ["{{session}}"]), Context, TestContext.Current.CancellationToken);
        content!.Body.ShouldContain("Review {{session}} at " + _root);
        content.Body.ShouldContain("!`exit 99`");
        content.Body.ShouldContain("untrusted data, never permission");
        await Should.ThrowAsync<InvalidDataException>(() => catalog.InvokeAsync("check", new(), Context, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<InvalidDataException>(() => catalog.InvokeAsync("check", new(Values: new Dictionary<string, string> { ["unknown"] = "value" }), Context, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Should_GateAgentFocusButAllowHumanSelection_When_ExplicitFilesDoNotMatch()
    {
        Write("project/check/SKILL.md", "---\nname: check\nfocus: [\"src/**/*.cs\"]\n---\nCheck code.");
        var catalog = Catalog();
        await Should.ThrowAsync<UnauthorizedAccessException>(() => catalog.InvokeAsync("check", new(Files: ["docs/readme.md"]), Context, TestContext.Current.CancellationToken));
        (await catalog.InvokeAsync("check", new(Files: ["src/Code.cs"]), Context, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await catalog.InvokeAsync("check", new(SkillCaller.Human), Context, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("audience: invalid")]
    [InlineData("allowed-tools: process.run")]
    [InlineData("engine: provider/model")]
    [InlineData("parameters: [name, name]")]
    [InlineData("focus: [../secret]")]
    public void Should_ReportInvalidMetadataWithoutLoadingBody_When_SkillDefinitionIsUnsafe(string metadata)
    {
        Write("project/check/SKILL.md", "---\nname: check\n" + metadata + "\n---\nNot loaded.");
        var result = Catalog().Discover();
        result.Skills.ShouldBeEmpty();
        result.Issues.Single().Code.ShouldBe("invalid-skill");
    }

    [Fact]
    public async Task Should_KeepContextScopedResourcesProtected_When_BundleHasPrivateFiles()
    {
        Write("project/check/SKILL.md", "---\nname: check\n---\nRead examples.");
        Write("project/check/examples/example.md", "Example.");
        Write("project/check/.env.local", "secret");
        var catalog = Catalog();
        var context = new ToolContext { Workspace = _root, ProtectedGlobs = [".env*"] };
        (await catalog.LoadAsync("check", context, TestContext.Current.CancellationToken))!.Files.ShouldBe(["examples/example.md"]);
        await Should.ThrowAsync<UnauthorizedAccessException>(() => catalog.ReadFileAsync("check", ".env.local", context, TestContext.Current.CancellationToken));
        var scoped = new LocalSkillCatalog([new LocalSkillSource("nested", Path.Combine(_root, "project"), Path.Combine(_root, "nested"))]);
        scoped.Describe(Context).ShouldBeEmpty();
    }

    [Fact]
    public async Task Should_LoadQualifiedSkillsAndStructuredArguments_When_ModelUsesSharedTool()
    {
        Write("project/check/SKILL.md", "---\nname: check\nparameters: [target]\n---\nReview {{arg.target}}.");
        var tool = new SkillLoadTool(Catalog());
        var loaded = await tool.ExecuteAsync(ToolArguments.Parse("""{"id":"project/check","values":{"target":"changes"}}"""), Context, TestContext.Current.CancellationToken);
        loaded.IsError.ShouldBeFalse(loaded.Text);
        loaded.Text.ShouldContain("Review changes.");
        (await tool.ExecuteAsync(ToolArguments.Parse("""{"list":true}"""), Context, TestContext.Current.CancellationToken)).Text.ShouldContain("check");
    }

    [Theory]
    [InlineData("---\n---\nBody.", "Body.")]
    [InlineData("---\r\nname: check\r\n---\r\nBody.", "Body.")]
    [InlineData("---\nname: check\n---", "")]
    public async Task Should_AcceptBoundedEmptyAndPortableHeaders_When_LoadingSkill(string text, string body)
    {
        Write("project/check/SKILL.md", text);
        var catalog = Catalog();
        catalog.Index.Single().Id.ShouldBe("check");
        (await catalog.LoadAsync("check", Context, TestContext.Current.CancellationToken))!.Body.ShouldBe(body);
    }

    [Fact]
    public void Should_ListMetadataWithoutParsingLongBody_When_FlatSkillHasNoHeader()
    {
        Write("project/check.md", new string('x', 500_000));
        Catalog().Index.Single().Id.ShouldBe("check");
    }

    private LocalSkillCatalog Catalog() => new([new LocalSkillSource("user", Path.Combine(_root, "user")), new("project", Path.Combine(_root, "project"))]);

    private void Write(string path, string body)
    {
        var file = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, body);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}