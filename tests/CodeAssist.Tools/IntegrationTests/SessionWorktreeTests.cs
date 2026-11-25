using CodeAssist.Persistence;
using CodeAssist.Tools;
using CodeAssist.Sessions;
using CodeAssist.Workspaces.Platform;
using CodeAssist.Contracts;
using CodeAssist.Workspaces;
using NSubstitute;
using Shouldly;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class SessionWorktreeTests
{
    [Fact]
    public async Task Should_FinishJournalCleanup_When_WorktreeDeletionAlreadyCompleted()
    {
        var root = Path.Combine(Path.GetTempPath(), "ca-worktree-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var journal = new JsonlJournalStore(Path.Combine(root, "journal"));
            var context = new ToolContext { Workspace = root, SessionId = Guid.NewGuid() };
            await journal.AppendAsync(new SessionStarted(context.SessionId, 0, default, root, null), 0, TestContext.Current.CancellationToken);
            await journal.AppendAsync(new WorktreeChanged(context.SessionId, 0, default, new(root, Path.Combine(root, "already-removed"), false, true)), 1, TestContext.Current.CancellationToken);
            var branches = Substitute.For<IWorkspaceBranches>();
            branches.ListAsync(Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkspaceBranch>());
            var recovered = await new JournalSessionWorktrees(journal, branches).ExitAsync(true, context, TestContext.Current.CancellationToken);
            recovered.CleanupPending.ShouldBeFalse();
            recovered.Directory.ShouldBeNull();
            await branches.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Should_AllowNestedDispatchWithoutDeadlock_When_RunningFlows()
    {
        var root = Path.GetTempPath();
        var context = new ToolContext { Workspace = root, SessionId = Guid.NewGuid() };
        var trees = Substitute.For<ISessionWorktrees>();
        trees.ReadAsync(Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(new SessionWorktree(root, null, false, false));
        var inner = Substitute.For<IToolDispatcher>();
        var wrapper = new WorkspaceToolDispatcher(inner, trees);
        inner.InvokeAsync("child", Arg.Any<string>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(ToolResult.Ok("child complete"));
        inner.InvokeAsync("flow.run", Arg.Any<string>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>())
            .Returns(_ => wrapper.InvokeAsync("child", "{}", context, TestContext.Current.CancellationToken));
        (await wrapper.InvokeAsync("flow.run", "{}", context, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Text.ShouldBe("child complete");
    }

    [Fact]
    public async Task Should_RecoverWorktreeStateAndPreserveDirtyFilesAndUniqueCommits_When_ExitingSessions()
    {
        var root = Path.Combine(Path.GetTempPath(), "ca-worktree-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? worktree = null;
        try
        {
            var runner = new ProcessRunner();
            var context = new ToolContext { Workspace = root, SessionId = Guid.NewGuid() };
            foreach (var command in new IReadOnlyList<string>[] { new[] { "init", "-q" }, new[] { "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--allow-empty", "-m", "initial", "-q" } })
                (await runner.RunAsync(new("git", command, root, TimeSpan.FromSeconds(10), 4096, []), TestContext.Current.CancellationToken)).ExitCode.ShouldBe(0);
            var journal = new JsonlJournalStore(Path.Combine(root, "journal"));
            await journal.AppendAsync(new SessionStarted(context.SessionId, 0, default, root, null), 0, TestContext.Current.CancellationToken);
            var branches = new GitWorkspaceBranches(runner);
            var sessions = new JournalSessionWorktrees(journal, branches);
            var state = await sessions.EnterAsync("review", context, TestContext.Current.CancellationToken);
            worktree = state.Directory;
            state.Active.ShouldBeTrue();
            var recovered = new JournalSessionWorktrees(journal, branches);
            (await recovered.ReadAsync(context, TestContext.Current.CancellationToken)).Directory.ShouldBe(worktree);
            await File.WriteAllTextAsync(Path.Combine(worktree!, "preserve.txt"), "keep", TestContext.Current.CancellationToken);
            await Should.ThrowAsync<IOException>(() => recovered.ExitAsync(true, context, TestContext.Current.CancellationToken));
            var pending = await recovered.ReadAsync(context, TestContext.Current.CancellationToken);
            pending.Active.ShouldBeFalse();
            pending.CleanupPending.ShouldBeTrue();
            File.Exists(Path.Combine(worktree!, "preserve.txt")).ShouldBeTrue();
            File.Delete(Path.Combine(worktree!, "preserve.txt"));
            (await recovered.ExitAsync(true, context, TestContext.Current.CancellationToken)).CleanupPending.ShouldBeFalse();
            Directory.Exists(worktree).ShouldBeFalse();
            worktree = (await recovered.EnterAsync("committed", context, TestContext.Current.CancellationToken)).Directory;
            await File.WriteAllTextAsync(Path.Combine(worktree!, "isolated.txt"), "preserve committed change", TestContext.Current.CancellationToken);
            foreach (var command in new IReadOnlyList<string>[] { new[] { "add", "isolated.txt" }, new[] { "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "isolated", "-q" } })
                (await runner.RunAsync(new("git", command, worktree!, TimeSpan.FromSeconds(10), 4096, []), TestContext.Current.CancellationToken)).ExitCode.ShouldBe(0);
            await Should.ThrowAsync<IOException>(() => recovered.ExitAsync(true, context, TestContext.Current.CancellationToken));
            Directory.Exists(worktree).ShouldBeTrue();
            (await recovered.ReadAsync(context, TestContext.Current.CancellationToken)).CleanupPending.ShouldBeTrue();
        }
        finally
        {
            if (worktree is not null && Directory.Exists(worktree)) Directory.Delete(worktree, true);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }
}