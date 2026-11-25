using CodeAssist.Workspaces.Platform;
using Shouldly;

namespace CodeAssist.Workspaces.Platform.IntegrationTests;

public sealed class PathGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ca-guard-" + Guid.NewGuid().ToString("N")[..8]);

    public PathGuardTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void RelativePathsStayInsideAndEscapesAreFlagged()
    {
        var guard = new PathGuard(_root, [".env*", ".git/**"]);

        guard.Resolve("src/a.cs").Outside.ShouldBeFalse();
        guard.Resolve("src/a.cs").Relative.ShouldBe("src/a.cs");
        guard.Resolve("../elsewhere/x").Outside.ShouldBeTrue();
        guard.Resolve(Path.Combine(Path.GetTempPath(), "other.txt")).Outside.ShouldBeTrue();
    }

    [Fact]
    public void ProtectedPatternsMatchAtAnyDepth()
    {
        var guard = new PathGuard(_root, [".env*", ".git/**"]);

        guard.Resolve("sub/.env.local").Protected.ShouldBeTrue();
        guard.Resolve(".git/config").Protected.ShouldBeTrue();
        guard.Resolve("src/env.txt").Protected.ShouldBeFalse();
    }

    [Fact]
    public void RejectsControlCharactersAndEmptyPaths()
    {
        var guard = new PathGuard(_root);

        Should.Throw<PathRejectedException>(() => guard.Resolve(""));
        Should.Throw<PathRejectedException>(() => guard.Resolve("a\0b"));
    }

    [Fact]
    public void SymlinkOutOfTheWorkspaceIsOutside()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ca-guard-out-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outside);
        try
        {
            try { Directory.CreateSymbolicLink(Path.Combine(_root, "link"), outside); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return; }

            new PathGuard(_root).Resolve("link/file.txt").Outside.ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }
}