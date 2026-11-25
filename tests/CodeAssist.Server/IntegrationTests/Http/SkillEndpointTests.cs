using CodeAssist.Extensions.Skills;
using CodeAssist.Configuration;
using CodeAssist.Server.Composition;
using System.Net;
using System.Net.Http.Json;
using CodeAssist.Tools;
using CodeAssist.Contracts;
using NSubstitute;
using Shouldly;

namespace CodeAssist.Server.IntegrationTests.Http;

public sealed class SkillEndpointTests
{
    [Theory]
    [InlineData("workspace/.code-assist/skills", "project")]
    [InlineData("profile/.code-assist/skills", "personal")]
    [InlineData("workspace/.agents/skills", "project-shared")]
    [InlineData("workspace/.claude/skills", "project-compat")]
    [InlineData("profile/.agents/skills", "personal-shared")]
    [InlineData("profile/.claude/skills", "personal-compat")]
    public async Task Should_DiscoverProjectAndPersonalSources_When_SkillFolderExists(string path, string origin)
    {
        using var fixture = new SkillSources();
        fixture.Write(path, "Source body.");
        var catalog = SkillComposition.CreateLocalCatalog(new(), fixture.Locations);
        var summary = catalog.Describe(fixture.Context, SkillCaller.Human).Single();
        summary.Id.ShouldBe("check");
        summary.Origin.ShouldBe(origin);
        (await catalog.LoadAsync(origin + "/check", fixture.Context, TestContext.Current.CancellationToken))!.Body.ShouldBe("Source body.");
        catalog.Discover(fixture.Context).Issues.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_PreferNativeSourcesAndKeepQualifiedAlternatives_When_SkillNamesCollide(bool managed)
    {
        using var fixture = new SkillSources();
        var sources = new (string Path, string Origin)[]
        {
            ("workspace/.code-assist/skills", "project"),
            ("profile/.code-assist/skills", "personal"),
            ("workspace/.agents/skills", "project-shared"),
            ("workspace/.claude/skills", "project-compat"),
            ("profile/.agents/skills", "personal-shared"),
            ("profile/.claude/skills", "personal-compat"),
            ("extra", "extra-0"),
        };
        foreach (var source in sources) fixture.Write(source.Path, source.Origin);
        if (managed) fixture.Write("managed/skills", "managed");
        var settings = new CodeAssistSettings();
        settings.Skills.Directories = [Path.Combine(fixture.Root, "extra")];
        var catalog = SkillComposition.CreateLocalCatalog(settings, fixture.Locations);
        (await catalog.LoadAsync("check", fixture.Context, TestContext.Current.CancellationToken))!.Body.ShouldBe(managed ? "managed" : "project");
        foreach (var source in sources)
            (await catalog.LoadAsync(source.Origin + "/check", fixture.Context, TestContext.Current.CancellationToken))!.Body.ShouldBe(source.Origin);
        catalog.Discover(fixture.Context).Issues.Count(issue => issue.Code == "shadowed-id").ShouldBe(managed ? 7 : 6);
        if (managed) (await catalog.LoadAsync("managed/check", fixture.Context, TestContext.Current.CancellationToken))!.Body.ShouldBe("managed");
    }

    [Fact]
    public async Task Should_PreferAncestorNativeSourceOverCompatibility_When_CurrentProjectHasNoNativeSkill()
    {
        using var fixture = new SkillSources();
        fixture.Write("workspace/.code-assist/skills", "Ancestor.");
        fixture.Write("workspace/nested/.agents/skills", "Compatibility.");
        fixture.Write("profile/.code-assist/skills", "Personal.");
        var locations = fixture.Locations with { WorkingDirectory = Path.Combine(fixture.Root, "workspace", "nested") };
        var context = new ToolContext { Workspace = locations.WorkingDirectory };
        var catalog = SkillComposition.CreateLocalCatalog(new(), locations);
        (await catalog.LoadAsync("check", context, TestContext.Current.CancellationToken))!.Body.ShouldBe("Ancestor.");
        (await catalog.LoadAsync("ancestor-1/check", context, TestContext.Current.CancellationToken))!.Body.ShouldBe("Ancestor.");
        (await catalog.LoadAsync("project-shared/check", context, TestContext.Current.CancellationToken))!.Body.ShouldBe("Compatibility.");
    }

    [Theory]
    [InlineData(".code-assist")]
    [InlineData("custom-home")]
    public async Task Should_KeepPersonalCompatibilitySourcesInsideProfile_When_CodeAssistHomeIsSelected(string home)
    {
        using var fixture = new SkillSources();
        fixture.Write("profile/.agents/skills", "Personal compatibility.");
        var locations = fixture.Locations with { Home = Path.Combine(fixture.Root, "profile", home) };
        var catalog = SkillComposition.CreateLocalCatalog(new(), locations);
        (await catalog.LoadAsync("personal-shared/check", fixture.Context, TestContext.Current.CancellationToken))!.Body.ShouldBe("Personal compatibility.");
    }

    [Theory]
    [InlineData("workspace/.agents/skills", "project-shared")]
    [InlineData("workspace/.claude/skills", "project-compat")]
    [InlineData("profile/.agents/skills", "personal-shared")]
    [InlineData("profile/.claude/skills", "personal-compat")]
    public async Task Should_ApplyLocalSkillPolicyToCompatibilitySources_When_AccessIsRestricted(string path, string origin)
    {
        using var fixture = new SkillSources();
        fixture.Write(path, "Restricted.");
        var settings = new CodeAssistSettings();
        settings.Policy.Skills.LocalFiles = false;
        var catalog = SkillComposition.CreateLocalCatalog(settings, fixture.Locations);
        catalog.Describe(fixture.Context, SkillCaller.Human).ShouldBeEmpty();
        await Should.ThrowAsync<UnauthorizedAccessException>(() => catalog.LoadAsync(origin + "/check", fixture.Context, TestContext.Current.CancellationToken));
        settings.Policy.Skills.LocalFiles = true;
        settings.Policy.Rules.Add(new() { Action = "skill.use", Resource = "check", Effect = RuleDecision.Deny });
        catalog.Describe(fixture.Context, SkillCaller.Human).ShouldBeEmpty();
        await Should.ThrowAsync<UnauthorizedAccessException>(() => catalog.LoadAsync(origin + "/check", fixture.Context, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Should_ListAndExpandExplicitHumanGuidance_When_SkillPortIsPresent()
    {
        var skills = Substitute.For<ISkillText>();
        skills.Commands.Returns([new("review", "Review changes.", "project", null, null, ["target"])]);
        skills.ExpandAsync("review", "task", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns("Review changes.");
        await using var server = await ServerFixture.StartAsync(skills: skills);
        (await server.Http.GetFromJsonAsync<SkillCommandDto[]>("/v1/skills/commands", TestContext.Current.CancellationToken))!.Single().Parameters.ShouldBe(["target"]);
        using var expanded = await server.Http.PostAsJsonAsync("/v1/skills/expand", new ExpandSkillRequest("review", "task", ["diff"]), TestContext.Current.CancellationToken);
        expanded.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await expanded.Content.ReadFromJsonAsync<SkillPromptDto>(TestContext.Current.CancellationToken))!.Text.ShouldBe("Review changes.");
        server.Clients.ShouldBeEmpty();
        using var openApi = await server.Http.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        (await openApi.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("/v1/skills/expand");
    }

    [Fact]
    public async Task Should_RejectInvalidAndRestrictedCallsWithoutLeakingPaths_When_SkillPortFails()
    {
        var skills = Substitute.For<ISkillText>();
        skills.ExpandAsync("review", "", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<string?>(new UnauthorizedAccessException("private/path")));
        await using var server = await ServerFixture.StartAsync(skills: skills);
        using var invalid = await server.Http.PostAsJsonAsync("/v1/skills/expand", new ExpandSkillRequest("../escape"), TestContext.Current.CancellationToken);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var denied = await server.Http.PostAsJsonAsync("/v1/skills/expand", new ExpandSkillRequest("review"), TestContext.Current.CancellationToken);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await denied.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain("private/path");
        using var execution = await server.Http.PostAsJsonAsync("/v1/skills/run", new { id = "review" }, TestContext.Current.CancellationToken);
        execution.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        server.Http.DefaultRequestHeaders.Authorization = null;
        using var unauthenticated = await server.Http.GetAsync("/v1/skills/commands", TestContext.Current.CancellationToken);
        unauthenticated.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private sealed class SkillSources : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ca-skill-sources-" + Guid.NewGuid().ToString("N"));
        public SettingsLocations Locations => new(Path.Combine(Root, "profile", ".code-assist"), Path.Combine(Root, "workspace"), Path.Combine(Root, "managed"));
        public ToolContext Context => new() { Workspace = Locations.WorkingDirectory };

        public SkillSources() => Directory.CreateDirectory(Path.Combine(Locations.WorkingDirectory, ".git"));

        public void Write(string path, string body)
        {
            var directory = Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar), "check");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "SKILL.md"), "---\nname: check\ndescription: Source check.\n---\n" + body);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}