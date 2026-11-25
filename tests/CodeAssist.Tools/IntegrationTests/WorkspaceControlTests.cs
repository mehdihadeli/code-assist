using CodeAssist.Tools;
using CodeAssist.Workspaces.Platform;
using CodeAssist.Workspaces;
using NSubstitute;
using Shouldly;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class WorkspaceControlTests
{
    [Fact]
    public async Task WorktreeLifecycleGuardsOwnershipDirtyFilesAndCurrentWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "ca-worktree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var runner = new ProcessRunner();
        var context = new ToolContext { Workspace = root, SessionId = Guid.NewGuid() };
        var service = new GitWorkspaceBranches(runner);
        string? destination = null;
        try
        {
            async Task Git(params string[] arguments)
            {
                var result = await runner.RunAsync(new("git", arguments, root, TimeSpan.FromSeconds(30), 10000, []), TestContext.Current.CancellationToken);
                result.ExitCode.ShouldBe(0, result.Output);
            }
            await Git("init");
            await Git("-c", "user.name=CodeAssist Tests", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false", "commit", "--allow-empty", "-m", "initial");
            var tool = new WorkspaceBranchTool(service);
            var plan = await tool.PlanAsync(ToolArguments.Parse("""{"operation":"create","name":"test"}"""), context, TestContext.Current.CancellationToken);
            plan.Effects.ShouldContain(effect => effect.Kind == ToolEffectKind.Write && effect.Outside);
            destination = await service.DestinationAsync("test", context, TestContext.Current.CancellationToken);
            var created = await service.CreateAsync("test", context, TestContext.Current.CancellationToken);
            created.Directory.ShouldBe(destination);
            (await service.ListAsync(context, TestContext.Current.CancellationToken)).ShouldContain(item => item.Managed && item.Directory == destination);
            await Should.ThrowAsync<IOException>(() => service.RemoveAsync(root, true, context, TestContext.Current.CancellationToken));
            var active = new ToolContext { Workspace = destination, SessionId = context.SessionId };
            await Should.ThrowAsync<IOException>(() => service.RemoveAsync(destination, true, active, TestContext.Current.CancellationToken));
            await File.WriteAllTextAsync(Path.Combine(destination, "untracked.txt"), "keep", TestContext.Current.CancellationToken);
            await Should.ThrowAsync<IOException>(() => service.RemoveAsync(destination, false, context, TestContext.Current.CancellationToken));
            await service.RemoveAsync(destination, true, context, TestContext.Current.CancellationToken);
            Directory.Exists(destination).ShouldBeFalse();
            await Should.ThrowAsync<ToolArgumentException>(() => tool.ExecuteAsync(ToolArguments.Parse("""{"operation":"create","name":"../escape"}"""), context, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (destination is not null && Directory.Exists(destination)) await service.RemoveAsync(destination, true, context, CancellationToken.None);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
            if (destination is not null && Directory.Exists(Path.GetDirectoryName(destination))) Directory.Delete(Path.GetDirectoryName(destination)!, true);
        }
    }

    [Fact]
    public async Task SessionToolDeclaresEffectsAndPreservesPendingBoundary()
    {
        var sessions = Substitute.For<ISessionControl>();
        var context = new ToolContext { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() };
        sessions.RelocateAsync(context.SessionId, Arg.Any<string>(), context, Arg.Any<CancellationToken>()).Returns(call =>
            new SessionChange(context.SessionId, "relocate", call.ArgAt<string>(1), true));
        var tool = new SessionManageTool(sessions);
        var arguments = ToolArguments.Parse("""{"operation":"relocate","directory":"next"}""");
        var plan = await tool.PlanAsync(arguments, context, TestContext.Current.CancellationToken);
        plan.Effects.ShouldContain(effect => effect.Kind == ToolEffectKind.Write);
        plan.Effects.ShouldContain(effect => effect.Kind == ToolEffectKind.Read);
        var result = await tool.ExecuteAsync(arguments, context, TestContext.Current.CancellationToken);
        result.Text.ShouldContain("\"Pending\":true");
        context.Workspace.ShouldBe(Path.GetTempPath());
        await Should.ThrowAsync<ToolArgumentException>(() => tool.ExecuteAsync(ToolArguments.Parse("""{"operation":"rename","title":" "}"""), context, TestContext.Current.CancellationToken));
    }
}