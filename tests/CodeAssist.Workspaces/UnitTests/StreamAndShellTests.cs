using CodeAssist.Workspaces.Platform;
using Shouldly;

namespace CodeAssist.Workspaces.Platform.UnitTests;

public sealed class StreamAndShellTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 4)]
    [InlineData(8192, 8192)]
    public async Task Should_ReadExactBudget_When_StreamFits(int size, int limit)
    {
        using var stream = new MemoryStream(new byte[size]);
        (await LimitedStreamReader.ReadAsync(stream, limit, TestContext.Current.CancellationToken)).Length.ShouldBe(size);
    }

    [Fact]
    public async Task Should_RejectExcessAndCancellation_When_ReadingBoundedStreams()
    {
        using var stream = new MemoryStream(new byte[5]);
        await Should.ThrowAsync<IOException>(() => LimitedStreamReader.ReadAsync(stream, 4, TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => LimitedStreamReader.ReadAsync(stream, 10, cancelled.Token));
    }

    [Theory]
    [InlineData("bash", "bash", "-c")]
    [InlineData("powershell", "pwsh", "-NoLogo")]
    [InlineData("powershell", "powershell", "-NoLogo")]
    public void Should_UseNonInteractiveArguments_When_SelectingShellProfiles(string profile, string path, string first)
    {
        ShellLocator.LocateProfile(profile, path).Arguments[0].ShouldBe(first);
    }

    [Theory]
    [InlineData("powershell", "bash")]
    [InlineData("bash", "pwsh")]
    [InlineData("unknown", "bash")]
    public void Should_RejectMismatchedProfiles_When_SelectingShells(string profile, string path) =>
        Should.Throw<ArgumentException>(() => ShellLocator.LocateProfile(profile, path));
}