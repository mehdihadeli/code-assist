using CodeAssist.Tools;
using CodeAssist.Sessions;
using CodeAssist.Workspaces.Platform;
using NSubstitute;
using Shouldly;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class OutputRetentionTests
{
    [Fact]
    public async Task Should_RetainShellAndResourceBodies_When_ProducingBoundedPreviews()
    {
        var root = Path.Combine(Path.GetTempPath(), "ca-output-producers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new FileToolOutputStore(Path.Combine(root, "outputs"));
            var context = new ToolContext { Workspace = root, SessionId = Guid.NewGuid(), Outputs = store, Limits = new() { OutputMaxLines = 1, MaxProcessOutputChars = 10 } };
            var token = TestContext.Current.CancellationToken;
            var body = "first line\nsecond line\nlast line";
            var runner = Substitute.For<IProcessRunner>();
            runner.RunAsync(Arg.Any<ProcessRequest>(), token).Returns(new ProcessOutcome(0, body, 0, false, false));
            var shell = await new ProcRunTool(runner).ExecuteAsync(ToolArguments.Parse("""{"command":"fixture"}"""), context, token);
            shell.IsError.ShouldBeFalse(shell.Text);
            (await store.ReadAsync(context.SessionId, shell.OutputId.ShouldNotBeNull(), 1, 10, token)).ShouldContain("first line");
            var source = Substitute.For<IResourceSource>();
            source.Servers.Returns(["fixture"]);
            source.ReadAsync("fixture", "record:1", context, token).Returns(Task.FromResult<IReadOnlyList<ResourceContent>?>([new("record:1", body)]));
            var resource = await new ResourceReadTool(source).ExecuteAsync(ToolArguments.Parse("""{"server":"fixture","uri":"record:1"}"""), context, token);
            resource.IsError.ShouldBeFalse(resource.Text);
            var output = new OutputReadTool(store);
            var page = await output.ExecuteAsync(ToolArguments.Parse($"{{\"id\":\"{resource.OutputId.ShouldNotBeNull()}\",\"offset\":3,\"limit\":1}}"), context, token);
            page.Text.ShouldContain("second line");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Should_RetainFullTextAndEnforceOwnership_When_PreviewIsTruncated()
    {
        var root = Path.Combine(Path.GetTempPath(), "ca-output-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileToolOutputStore(root);
            var session = Guid.NewGuid();
            var context = new ToolContext { Workspace = Path.GetTempPath(), SessionId = session, Outputs = store, Limits = new() { OutputMaxLines = 1 } };
            var result = await ToolOutputRetention.CompleteAsync(ToolResult.Ok("first\nsecond\nthird"), context, TestContext.Current.CancellationToken);
            var id = result.OutputId.ShouldNotBeNull();
            result.Text.ShouldContain("output.read");
            (await new FileToolOutputStore(root).ReadAsync(session, id, 2, 1, TestContext.Current.CancellationToken)).ShouldContain("2: second");
            await Should.ThrowAsync<IOException>(() => store.ReadAsync(Guid.NewGuid(), id, 1, 10, TestContext.Current.CancellationToken));
            var longLine = await store.SaveAsync(session, new string('x', 6000) + "recovered tail", TestContext.Current.CancellationToken);
            (await store.ReadAsync(session, longLine, 1, 1, TestContext.Current.CancellationToken)).ShouldContain("line clipped");
            (await store.ReadCharactersAsync(session, longLine, 6000, 100, TestContext.Current.CancellationToken)).ShouldBe("recovered tail");
            await Should.ThrowAsync<IOException>(() => store.ReadCharactersAsync(Guid.NewGuid(), longLine, 0, 10, TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}