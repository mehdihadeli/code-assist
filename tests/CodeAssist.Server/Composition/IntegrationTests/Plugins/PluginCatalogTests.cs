using CodeAssist.Persistence;
using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Extensions.Plugins;
using CodeAssist.Contracts;
using NSubstitute;
using Shouldly;

namespace CodeAssist.Server.Composition.IntegrationTests.Plugins;

public sealed class PluginCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "code-assist-plugin-catalog-" + Guid.NewGuid().ToString("N"));
    private PluginStore Store => new(Path.Combine(_root, "store"));
    private ToolContext Context => new() { Workspace = _root, SessionId = Guid.NewGuid() };

    [Fact]
    public void Should_RestrictSequencesByReviewedContent_When_OrganizationDoesNotAdmitUserHooks()
    {
        Install("hook", "{\"event\":\"tool.before\",\"steps\":[{\"tool\":\"fs.read\",\"input\":{}}]}", "inspect.json");
        var package = Store.Inspect("team.review");
        var settings = new CodeAssist.Configuration.CodeAssistSettings();
        settings.Policy.Plugins.SequenceDigests = [];
        PluginComposition.Restriction(settings, package).ShouldNotBeNull();
        settings.Policy.Plugins.SequenceDigests = [package.Digest];
        PluginComposition.Restriction(settings, package).ShouldBeNull();
        settings.Policy.Rules.Add(new() { Action = "plugin.use", Resource = "team.*", Effect = CodeAssist.Configuration.RuleDecision.Deny });
        PluginComposition.Restriction(settings, package).ShouldNotBeNull();
    }

    [Fact]
    public async Task Should_LoadNamespacedSkillsAndAssets_When_EnabledAndAllowed()
    {
        Install("skill", "Review changes.", "inspect.md");
        var catalog = new PluginCatalog(Store, _root);
        catalog.Index.Single().Id.ShouldBe("team.review/inspect");
        var content = await catalog.LoadAsync("team.review/inspect", Context, CancellationToken.None);
        content.ShouldNotBeNull().Body.ShouldBe("Review changes.");
        content.Files.ShouldBe(["notes.md"]);
        (await catalog.ReadFileAsync(content.Id, "notes.md", Context, CancellationToken.None)).ShouldBe("Supporting notes.");
        (await catalog.ReadFileAsync(content.Id, "../outside.md", Context, CancellationToken.None)).ShouldBeNull();
        catalog.Entries.Single().ContentBytes.ShouldBe(15);
        Store.Usage()[content.Id].Invocations.ShouldBe(1);
        Store.SetEnabled("team.review", false);
        catalog.Index.ShouldBeEmpty();
        (await catalog.LoadAsync(content.Id, Context, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public void Should_ExcludeRestrictedPlugins_When_PolicyDeniesUse()
    {
        Install("prompt", "Review this task.", "inspect.md");
        var catalog = new PluginCatalog(Store, _root, permitted: _ => false);
        catalog.Entries.ShouldBeEmpty();
        Should.Throw<KeyNotFoundException>(() => catalog.Prompt("team.review/inspect", "task"));
    }

    [Fact]
    public void Should_ResolveRestrictedDelegationRole_When_PluginRoleIsEnabled()
    {
        Install("role", "Inspect code using read-only tools.", "inspect.md");
        var settings = new CodeAssist.Configuration.CodeAssistSettings();
        var catalog = new PluginCatalog(Store, _root);
        var role = PluginComposition.ResolveRole(settings, catalog, "team.review/inspect");
        role.ShouldNotBeNull().Delegatable.ShouldBeTrue();
        role.Tools.Allow.ShouldBe(["fs.read"]);
        settings.Roles.ShouldNotContainKey("team.review/inspect");
        Store.SetEnabled("team.review", false);
        PluginComposition.ResolveRole(settings, catalog, "team.review/inspect").ShouldBeNull();
    }

    [Fact]
    public async Task Should_DispatchEveryStepThroughAuthorization_When_WorkflowRuns()
    {
        Install("workflow", "{\"steps\":[{\"tool\":\"fs.read\",\"input\":{\"path\":\"README.md\"}},{\"tool\":\"fs.read\",\"input\":{\"path\":\"next.md\"}}]}", "inspect.json");
        var dispatcher = Substitute.For<IToolDispatcher>();
        dispatcher.Describe("fs.read", Arg.Any<ToolContext>()).Returns(new ToolSpec("fs.read", "Read", "{}", CodeAssist.Contracts.ToolRisk.Read, ["read"]));
        dispatcher.InvokeAsync("fs.read", Arg.Any<string>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(ToolResult.Error("denied"));
        var results = await new PluginCatalog(Store, _root).ExecuteAsync("team.review/inspect", dispatcher, Context, CancellationToken.None);
        results.Count.ShouldBe(1);
        results.Single().IsError.ShouldBeTrue();
        await dispatcher.Received(1).InvokeAsync("fs.read", Arg.Any<string>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_BlockRequestedTool_When_PreHookIsDenied()
    {
        Install("hook", "{\"event\":\"tool.before\",\"steps\":[{\"tool\":\"fs.read\",\"input\":{}}]}", "inspect.json");
        var inner = Substitute.For<IToolDispatcher>();
        inner.Describe(Arg.Any<string>(), Arg.Any<ToolContext>()).Returns(new ToolSpec("fs.read", "Read", "{}", CodeAssist.Contracts.ToolRisk.Read, ["read"]));
        inner.InvokeAsync("fs.read", Arg.Any<string>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(ToolResult.Error("denied"));
        var result = await new PluginToolDispatcher(inner, new PluginCatalog(Store, _root)).InvokeAsync("fs.write", "{}", Context, CancellationToken.None);
        result.IsError.ShouldBeTrue();
        await inner.DidNotReceive().InvokeAsync("fs.write", Arg.Any<string>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_KeepHookWithinParentSnapshot_When_ChildRoleHasBroaderTools()
    {
        Install("hook", "{\"event\":\"tool.before\",\"steps\":[{\"tool\":\"fs.read\",\"input\":{}}]}", "inspect.json");
        var context = new ToolContext { Workspace = _root, SessionId = Guid.NewGuid(), Role = "review" };
        var inner = Substitute.For<IToolDispatcher>();
        inner.Describe(Arg.Any<string>(), context).Returns(new ToolSpec("fs.write", "Write", "{}", ToolRisk.Write, ["write"]));
        var journal = Substitute.For<IJournalStore>();
        journal.ReadAsync(context.SessionId, Arg.Any<CancellationToken>()).Returns([new DelegationLinked(context.SessionId, 1, default,
            Guid.NewGuid(), Guid.NewGuid(), "review", "fixture/chat", 1, ["fs.write"])]);
        var dispatcher = new PluginToolDispatcher(inner, new PluginCatalog(Store, _root), journal);
        await Should.ThrowAsync<UnauthorizedAccessException>(() => dispatcher.InvokeAsync("fs.write", "{}", context, CancellationToken.None));
        await inner.DidNotReceive().InvokeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Should_ResolveWorkspaceRoleOverrides_When_WorkspaceDiffersFromDefault()
    {
        Install("role", "Inspect code using read-only tools.", "inspect.md");
        var catalog = new PluginCatalog(Store, _root);
        var settings = new CodeAssist.Configuration.CodeAssistSettings();
        var other = Path.Combine(_root, "other");
        Store.SetEnabled("team.review", false, workspace: other);
        PluginComposition.Roles(settings, catalog).ShouldContainKey("team.review/inspect");
        PluginComposition.Roles(settings, catalog, other).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("workflow", "{\"steps\":[{\"tool\":\"proc.run\",\"input\":{}}]}")]
    [InlineData("hook", "{\"event\":\"unknown\",\"steps\":[{\"tool\":\"fs.read\",\"input\":{}}]}")]
    [InlineData("language", "{\"program\":\"server\",\"languages\":{}}")]
    [InlineData("mcp", "{\"program\":\"server\",\"install\":\"arbitrary code\"}")]
    [InlineData("mcp", "{\"program\":\"server\",\"environment\":[\"TOKEN=secret\"]}")]
    public void Should_RejectInvalidDefinitions_When_PackageIsValidated(string kind, string definition)
    {
        Should.Throw<InvalidDataException>(() => Install(kind, definition, "inspect.json"));
        Store.List().ShouldBeEmpty();
    }

    private void Install(string kind, string body, string entry)
    {
        Directory.CreateDirectory(_root);
        var manifest = new PluginManifest { Id = "team.review", Release = "1.0.0", Summary = "Review guidance", Assets = ["notes.md"], Contributions =
            [new() { Key = "inspect", Kind = kind, Entry = entry, Description = "Inspect changes", Tools = ["fs.read"] }] };
        File.WriteAllText(Path.Combine(_root, PluginManifest.FileName), JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        File.WriteAllText(Path.Combine(_root, entry), body);
        File.WriteAllText(Path.Combine(_root, "notes.md"), "Supporting notes.");
        var package = PluginPackage.Open(_root);
        Store.Install([package]);
        Store.SetEnabled(manifest.Id, true, package.Digest);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}