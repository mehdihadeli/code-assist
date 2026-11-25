using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Sessions;
using CodeAssist.Configuration;
using NSubstitute;
using Shouldly;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class ConfigurationToolTests
{
    [Fact]
    public async Task Should_RespectRevisionsRedactionAndEnvironmentPrecedence_When_EditingRealSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "ca-config-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var environment = new Dictionary<string, string> { ["Appearance__Animations"] = "false", ["Providers__default__ApiKey"] = "fixture-not-a-real-key" };
            var locations = new SettingsLocations(Path.Combine(root, "home"), root, Path.Combine(root, "managed"));
            var settings = new SettingsService(locations, new DictionaryEnvironmentVariables(environment), new FlagOverrides());
            var access = new SettingsToolAccess(settings);
            var before = await access.ReadAsync(null, TestContext.Current.CancellationToken);
            before.GetRawText().ShouldNotContain("fixture-not-a-real-key");
            var revision = before.GetProperty("revision").GetString()!;
            await access.ChangeAsync("set", "appearance.animations", JsonSerializer.SerializeToElement(true), revision, TestContext.Current.CancellationToken);
            settings.Current.Settings.Appearance.Animations.ShouldBeFalse();
            (await File.ReadAllTextAsync(locations.UserFile, TestContext.Current.CancellationToken)).ShouldContain("true");
            await Should.ThrowAsync<UnauthorizedAccessException>(() => access.ChangeAsync("set", "agent.permissionPreset", JsonSerializer.SerializeToElement("acceptEdits"), settings.Current.Id, TestContext.Current.CancellationToken));
            await Should.ThrowAsync<InvalidOperationException>(() => access.ChangeAsync("set", "appearance.animations", JsonSerializer.SerializeToElement(false), "stale", TestContext.Current.CancellationToken));
            await access.ChangeAsync("unset", "appearance.animations", null, settings.Current.Id, TestContext.Current.CancellationToken);
            (await File.ReadAllTextAsync(locations.UserFile, TestContext.Current.CancellationToken)).ShouldNotContain("fixture-not-a-real-key");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Should_SeparateReadAndWriteEffects_When_ManagingConfiguration()
    {
        var access = Substitute.For<IConfigurationAccess>();
        access.ReadAsync(null, Arg.Any<CancellationToken>()).Returns(JsonSerializer.SerializeToElement(new { revision = "one" }));
        var tool = new ConfigManageTool(access);
        var context = new ToolContext { Workspace = Path.GetTempPath() };
        var read = ToolArguments.Parse("""{"operation":"get"}""");
        (await tool.PlanAsync(read, context, TestContext.Current.CancellationToken)).Effects.Single().Kind.ShouldBe(ToolEffectKind.Read);
        (await tool.ExecuteAsync(read, context, TestContext.Current.CancellationToken)).Text.ShouldContain("one");
        var write = ToolArguments.Parse("""{"operation":"set","path":"appearance.animations","value":false,"revision":"one"}""");
        (await tool.PlanAsync(write, context, TestContext.Current.CancellationToken)).Effects.Single().Kind.ShouldBe(ToolEffectKind.Write);
        await Should.ThrowAsync<ToolArgumentException>(() => tool.ExecuteAsync(ToolArguments.Parse("""{"operation":"set","path":"x"}"""), context, TestContext.Current.CancellationToken));
    }
}