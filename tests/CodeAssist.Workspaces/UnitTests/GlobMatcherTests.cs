using Shouldly;

namespace CodeAssist.Workspaces.Platform.UnitTests;

public sealed class GlobMatcherTests
{
    [Theory]
    [InlineData("**/*.cs", "a/b/c.cs", true)]
    [InlineData("**/*.cs", "c.cs", true)]
    [InlineData("*.cs", "a/c.cs", true)]
    [InlineData("src/*.cs", "src/a/c.cs", false)]
    [InlineData("{a,b}/x", "b/x", true)]
    [InlineData("node_modules/**", "node_modules/p/i.js", true)]
    public void GlobMatching(string pattern, string path, bool expected) =>
        new GlobMatcher([pattern]).IsMatch(path).ShouldBe(expected);

    [Fact]
    public void GlobExcludesWholeDirectories() =>
        new GlobMatcher(["node_modules/**"]).ExcludesDirectory("node_modules").ShouldBeTrue();
}