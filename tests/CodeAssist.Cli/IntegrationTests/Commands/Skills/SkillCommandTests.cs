using System.Text.Json.Nodes;
using CodeAssist.Cli.Hosting;
using CodeAssist.Cli.IntegrationTests.Support;
using Shouldly;

namespace CodeAssist.Cli.IntegrationTests.Commands.Skills;

public sealed class SkillCommandTests : IDisposable
{
    private readonly CliHarness _cli = new();

    [Fact]
    public async Task Should_ListAndExpandHumanSkillsWithoutInference_When_SourceUsesNativeMetadata()
    {
        Write("project", "---\nname: deploy\naudience: human\nparameters: [target]\n---\nDeploy {{arg.target}}. Input {{input}}.");
        (await _cli.Run("skills", "list", "--json")).Output.ShouldContain("deploy");
        (await _cli.Run("skills", "list", "--agent", "--json")).Output.ShouldNotContain("deploy");
        var expanded = await _cli.Run("skills", "expand", "project/deploy", "--arg", "staging", "--input", "reviewed", "--json");
        expanded.Code.ShouldBe(0, expanded.Output);
        JsonNode.Parse(expanded.Output)!["body"]!.GetValue<string>().ShouldContain("Deploy staging. Input reviewed.");
        (await _cli.Run("skills", "expand", "deploy", "--agent")).Code.ShouldBe(ExitCodes.UsageOrConfig);
        File.Exists(_cli.UserFile).ShouldBeFalse();
    }

    [Fact]
    public async Task Should_ApplyEnvironmentPreferencesAndManagedPolicy_When_QualifiedAliasIsUsed()
    {
        Write("project", "---\nname: check\n---\nCheck code.");
        _cli.Environment["Skills__Entries__check__Display"] = "hidden";
        (await _cli.Run("skills", "expand", "project/check")).Code.ShouldBe(ExitCodes.UsageOrConfig);
        _cli.Environment.Clear();
        File.WriteAllText(Path.Combine(_cli.Files.Managed, "settings.json"), """{"policy":{"rules":[{"action":"skill.use","resource":"check","effect":"deny"}]}}""");
        (await _cli.Run("skills", "expand", "project/check")).Code.ShouldBe(ExitCodes.UsageOrConfig);
        File.Exists(_cli.UserFile).ShouldBeFalse();
    }

    [Fact]
    public async Task Should_ExplainMalformedSkillsAndShadowing_When_DiagnosticsAreRequested()
    {
        Write("project", "---\nname: check\n---\nProject.");
        var personal = Path.Combine(_cli.Files.Home, "skills", "check");
        Directory.CreateDirectory(personal);
        File.WriteAllText(Path.Combine(personal, "SKILL.md"), "---\nname: check\n---\nPersonal.");
        var diagnose = await _cli.Run("skills", "diagnose", "--json");
        diagnose.Code.ShouldBe(0, diagnose.Output);
        diagnose.Output.ShouldContain("shadowed-id");
        File.WriteAllText(Path.Combine(personal, "SKILL.md"), "---\nname: check\naudience: invalid\n---\nBad.");
        (await _cli.Run("skills", "diagnose", "--json")).Code.ShouldBe(ExitCodes.UsageOrConfig);
    }

    [Fact]
    public async Task Should_KeepManagedSkillSwitchesRestrictive_When_EnvironmentRequestsEnablement()
    {
        Write("project", "---\nname: check\n---\nLocal instructions.");
        File.WriteAllText(Path.Combine(_cli.Files.Managed, "settings.json"), """{"policy":{"skills":{"localFiles":false,"standardGuides":false}}}""");
        _cli.Environment["Policy__Skills__LocalFiles"] = "true";
        _cli.Environment["Policy__Skills__StandardGuides"] = "true";
        _cli.Environment["Skills__StandardGuides"] = "true";
        var listed = await _cli.Run("skills", "list", "--json");
        listed.Code.ShouldBe(0, listed.Output);
        JsonNode.Parse(listed.Output)!.AsArray().ShouldBeEmpty();
        (await _cli.Run("skills", "expand", "project/check")).Code.ShouldBe(ExitCodes.UsageOrConfig);
        (await _cli.Run("skills", "expand", "standard/risk-check")).Code.ShouldBe(ExitCodes.UsageOrConfig);
        File.Exists(_cli.UserFile).ShouldBeFalse();
    }

    [Fact]
    public async Task Should_PreserveQualifiedProjectSelection_When_ManagedAndPersonalSourcesWin()
    {
        Write("project", "---\nname: check\n---\nProject instructions.");
        foreach (var (root, text) in new[] { (_cli.Files.Home, "Personal instructions."), (_cli.Files.Managed, "Managed instructions.") })
        {
            var directory = Path.Combine(root, "skills", "check");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "SKILL.md"), "---\nname: check\n---\n" + text);
        }
        (await _cli.Run("skills", "expand", "check")).Output.ShouldContain("Managed instructions.");
        (await _cli.Run("skills", "expand", "personal/check")).Output.ShouldContain("Personal instructions.");
        (await _cli.Run("skills", "expand", "project/check")).Output.ShouldContain("Project instructions.");
    }

    private void Write(string source, string text)
    {
        var directory = Path.Combine(_cli.Files.Locations.WorkingDirectory, ".code-assist", "skills", source);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), text);
    }

    public void Dispose() => _cli.Dispose();
}