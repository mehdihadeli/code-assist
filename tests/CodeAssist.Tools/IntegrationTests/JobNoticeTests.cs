using CodeAssist.Persistence;
using CodeAssist.Tools;
using CodeAssist.Sessions;
using CodeAssist.Contracts;
using Shouldly;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class JobNoticeTests
{
    [Fact]
    public async Task Should_PersistBeforePublishingAndAcknowledgeByOwner_When_BackgroundWorkFinishes()
    {
        var root = Path.Combine(Path.GetTempPath(), "ca-notices-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new JsonlJournalStore(root);
            var context = new ToolContext { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() };
            var token = TestContext.Current.CancellationToken;
            await journal.AppendAsync(new SessionStarted(context.SessionId, 0, default, context.Workspace, null), 0, token);
            var notices = new JournalJobNotices(journal);
            await using var jobs = new InMemoryToolJobs(persistCompletion: notices.RecordAsync);
            var completion = new TaskCompletionSource<ToolJob>(TaskCreationOptions.RunContinuationsAsynchronously);
            jobs.Completed += job => completion.TrySetResult(job);
            jobs.Start("Background fixture", _ => Task.FromResult(ToolResult.Ok("Finished")), context, token);
            var finished = await completion.Task.WaitAsync(token);
            var reopened = new JournalJobNotices(new JsonlJournalStore(root));
            (await reopened.ReadAsync(context.SessionId, token)).Single().JobId.ShouldBe(finished.Id);
            await notices.RecordAsync(finished, token);
            (await notices.ReadAsync(context.SessionId, token)).Count.ShouldBe(1);
            await Should.ThrowAsync<UnauthorizedAccessException>(() => reopened.AcknowledgeAsync(Guid.NewGuid(), finished.Id, token));
            await reopened.AcknowledgeAsync(context.SessionId, finished.Id, token);
            (await notices.ReadAsync(context.SessionId, token)).ShouldBeEmpty();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}