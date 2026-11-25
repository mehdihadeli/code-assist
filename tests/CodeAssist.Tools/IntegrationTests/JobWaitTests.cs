using CodeAssist.Tools;
using CodeAssist.Sessions;
using Shouldly;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class JobWaitTests
{
    private static readonly ToolContext Context = new() { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() };

    [Fact]
    public async Task Should_ReturnCompletionAndIsolateSessions_When_JobIsAwaited()
    {
        await using var jobs = new InMemoryToolJobs();
        var release = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = jobs.Start("Wait test", token => release.Task.WaitAsync(token), Context, TestContext.Current.CancellationToken);
        var tool = new JobTool(jobs, JobOperation.Wait);
        var arguments = ToolArguments.Parse($$"""{"id":"{{job.Id}}","seconds":5}""");
        var waiting = tool.ExecuteAsync(arguments, Context, TestContext.Current.CancellationToken);
        release.SetResult(ToolResult.Ok("Finished"));
        var completed = await waiting;
        completed.IsError.ShouldBeFalse();
        completed.Text.ShouldContain("Completed");
        completed.Text.ShouldContain("Finished");
        var foreign = new ToolContext { Workspace = Context.Workspace, SessionId = Guid.NewGuid() };
        (await tool.ExecuteAsync(arguments, foreign, TestContext.Current.CancellationToken)).IsError.ShouldBeTrue();
        (await tool.PlanAsync(arguments, Context, TestContext.Current.CancellationToken)).Effects.ShouldAllBe(effect => effect.Kind == ToolEffectKind.Read);
    }

    [Fact]
    public async Task Should_LeaveJobRunning_When_WaitTimesOutOrIsCancelled()
    {
        await using var jobs = new InMemoryToolJobs();
        var release = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = jobs.Start("Wait timeout", token => release.Task.WaitAsync(token), Context, TestContext.Current.CancellationToken);
        var tool = new JobTool(jobs, JobOperation.Wait);
        var arguments = ToolArguments.Parse($$"""{"id":"{{job.Id}}","seconds":1}""");
        var timedOut = await tool.ExecuteAsync(arguments, Context, TestContext.Current.CancellationToken);
        timedOut.Text.ShouldContain("Running");
        jobs.Read(Context.SessionId, job.Id).ShouldNotBeNull().Status.ShouldBe(ToolJobStatus.Running);
        using var cancelled = new CancellationTokenSource();
        var waiting = tool.ExecuteAsync(arguments, Context, cancelled.Token);
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => waiting);
        jobs.Read(Context.SessionId, job.Id).ShouldNotBeNull().Status.ShouldBe(ToolJobStatus.Running);
        release.SetResult(ToolResult.Ok("Done"));
        await tool.ExecuteAsync(arguments, Context, TestContext.Current.CancellationToken);
        await Should.ThrowAsync<ToolArgumentException>(() => tool.ExecuteAsync(ToolArguments.Parse($$"""{"id":"{{job.Id}}","seconds":121}"""), Context, TestContext.Current.CancellationToken));
    }
}