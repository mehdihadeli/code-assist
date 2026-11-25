using CodeAssist.Tools;
using CodeAssist.Workspaces.Platform;
using NSubstitute;
using Shouldly;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class ShellExecutionTests
{
    [Fact]
    public async Task Should_BoundUnterminatedOutput_When_RunningInstalledShellProfiles()
    {
        foreach (var profile in OperatingSystem.IsWindows() ? new[] { "bash", "powershell" } : new[] { "bash" })
        {
            var gitBash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
            var shell = ShellLocator.LocateProfile(profile, profile == "bash" && File.Exists(gitBash) ? gitBash : null);
            var command = profile == "bash" ? "printf '%*s' 200000 ''" : "[Console]::Write(('x' * 200000))";
            var result = await new ProcessRunner().RunAsync(new(shell.FilePath, [.. shell.Arguments, command], Path.GetTempPath(), TimeSpan.FromSeconds(15), 128, []), TestContext.Current.CancellationToken);
            result.ExitCode.ShouldBe(0, profile + ": " + result.Output);
            result.Output.Length.ShouldBe(128);
            result.OmittedChars.ShouldBe(200000 - 128);
            result.TimedOut.ShouldBeFalse();
            result.Cancelled.ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData("bash", "bash", "-c")]
    [InlineData("powershell", "pwsh", "-NoLogo")]
    public async Task Should_UseExplicitShellProfileAndSharedLimits_When_ExecutingCommands(string profile, string executable, string firstArgument)
    {
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>()).Returns(new ProcessOutcome(0, "done", 0, false, false));
        var tool = new ProcRunTool(runner);
        var context = new ToolContext { Workspace = Path.GetTempPath(), Limits = new() { Shells = new Dictionary<string, string> { [profile] = executable } } };
        var arguments = ToolArguments.Parse(System.Text.Json.JsonSerializer.Serialize(new { command = "echo done", shell = profile }));
        (await tool.PlanAsync(arguments, context, TestContext.Current.CancellationToken)).Effects.ShouldContain(effect => effect.Target == "shell:" + profile);
        (await tool.ExecuteAsync(arguments, context, TestContext.Current.CancellationToken)).IsError.ShouldBeFalse();
        await runner.Received().RunAsync(Arg.Is<ProcessRequest>(request => request.FilePath == executable && request.Arguments[0] == firstArgument && request.Timeout == context.Limits.ProcessTimeout), Arg.Any<CancellationToken>());
        await Should.ThrowAsync<ToolArgumentException>(() => tool.ExecuteAsync(ToolArguments.Parse("""{"command":"echo test","timeoutSeconds":86400}"""), context, TestContext.Current.CancellationToken));
    }
}