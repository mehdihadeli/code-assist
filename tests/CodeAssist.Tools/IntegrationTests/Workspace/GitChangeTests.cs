using CodeAssist.Tools;
using CodeAssist.Workspaces.Platform;
using CodeAssist.Workspaces;
using Shouldly;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class GitChangeTests
{
    [Fact]
    public async Task Should_SeparateIndexAndWorkingChangesAndHideProtectedPaths_When_InspectingGit()
    {
        var root = Path.Combine(Path.GetTempPath(), "ca-changes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var runner = new ProcessRunner();
        var token = TestContext.Current.CancellationToken;
        async Task Git(params string[] arguments)
        {
            var result = await runner.RunAsync(new("git", arguments, root, TimeSpan.FromSeconds(15), 10000, []), token);
            result.ExitCode.ShouldBe(0, result.Output);
        }
        try
        {
            await Git("init");
            await Git("config", "user.name", "Fixture");
            await Git("config", "user.email", "fixture@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(root, "file.txt"), "first\n", token);
            await File.WriteAllTextAsync(Path.Combine(root, ".env.tracked"), "private rename source\n", token);
            await Git("add", "file.txt", ".env.tracked");
            await Git("commit", "-m", "fixture");
            await File.WriteAllTextAsync(Path.Combine(root, "file.txt"), "staged\n", token);
            await Git("add", "file.txt");
            await File.WriteAllTextAsync(Path.Combine(root, "file.txt"), "working\n", token);
            await File.WriteAllTextAsync(Path.Combine(root, "new file.txt"), "new", token);
            await File.WriteAllTextAsync(Path.Combine(root, ".env.local"), "secret", token);
            await Git("mv", ".env.tracked", "renamed-secret.txt");
            var context = new ToolContext { Workspace = root, ProtectedGlobs = [".env*"] };
            var service = new GitWorkspaceChanges(runner);
            var all = await service.ReadAsync("all", true, context, token);
            all.Count.ShouldBe(2);
            all.ShouldNotContain(change => change.Path == "renamed-secret.txt");
            var file = all.Single(change => change.Path == "file.txt");
            file.IndexDiff.ShouldNotBeNull().ShouldContain("+staged");
            file.WorktreeDiff.ShouldNotBeNull().ShouldContain("+working");
            (await service.ReadAsync("staged", false, context, token)).Single().Path.ShouldBe("file.txt");
            (await service.ReadAsync("conflicts", false, context, token)).ShouldBeEmpty();
            (await File.ReadAllTextAsync(Path.Combine(root, "file.txt"), token)).ShouldBe("working\n");
        }
        finally
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }
}