using CodeAssist.Persistence;
using CodeAssist.Extensions.Skills;
using CodeAssist.Tools;
using CodeAssist.Sessions;
using CodeAssist.Contracts;
using CodeAssist.Workspaces;
using CodeAssist.Tools.Extensions;
using System.Net;
using System.Text;
using Shouldly;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class SessionJournalTests
{
    [Fact]
    public async Task QuestionBrokerSupportsAnswersDismissalCancellationAndSessionIsolation()
    {
        var broker = new QuestionBroker();
        var context = new ToolContext { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() };
        Question[] questions = [new(null, "Which?", [new("One")], false)];
        (await broker.AskAsync(questions, context, TestContext.Current.CancellationToken)).ShouldBeNull();
        broker.Requested += request =>
        {
            broker.Answer(Guid.NewGuid(), request.Id, null).ShouldBeFalse();
            broker.Answer(context.SessionId, request.Id, [new(["One"], null)]).ShouldBeTrue();
        };
        (await broker.AskAsync(questions, context, TestContext.Current.CancellationToken))!.Single().Choices.Single().ShouldBe("One");
        var waiting = new QuestionBroker();
        waiting.Requested += _ => { };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = waiting.AskAsync(questions, context, cancellation.Token);
        waiting.PendingFor(context.SessionId).Count.ShouldBe(1);
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => pending);
        waiting.PendingFor(context.SessionId).ShouldBeEmpty();
    }

    [Fact]
    public async Task ConfigurableSearchEncodesQueryAndRejectsUnsafeResultUrls()
    {
        using var handler = new SearchHandler();
        using var http = new HttpClient(handler);
        using var search = new SearxngWebSearch(new("https://search.invalid/search"), http: http);
        var result = await search.SearchAsync("a & b", TestContext.Current.CancellationToken);
        result.Single().Url.ShouldBe("https://result.invalid/");
        handler.Query.ShouldContain("q=a%20%26%20b");
    }

    private sealed class SearchHandler : HttpMessageHandler
    {
        public string Query { get; private set; } = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Query = request.RequestUri!.Query;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"results\":[{\"title\":\"Result\",\"url\":\"https://result.invalid\",\"content\":\"Snippet\"},{\"url\":\"javascript:alert(1)\"}]}", Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Should_BlockAgentBodyAndResources_When_SkillAudienceIsHuman()
    {
        var root = Path.Combine(Path.GetTempPath(), "ca-skill-access-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "SKILL.md"), "---\nname: deploy\ndescription: Deploy reviewed changes.\naudience: human\n---\nDeploy safely.");
            File.WriteAllText(Path.Combine(root, "notes.md"), "Private manual instructions.");
            var catalog = new LocalSkillCatalog([root]);
            catalog.Index.ShouldBeEmpty();
            var context = new ToolContext { Workspace = root };
            await Should.ThrowAsync<UnauthorizedAccessException>(() => catalog.LoadAsync("deploy", context, TestContext.Current.CancellationToken));
            await Should.ThrowAsync<UnauthorizedAccessException>(() => catalog.ReadFileAsync("deploy", "notes.md", context, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LocalSkillsLoadYamlBodyAndBundleWithoutExecutingFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "ca-skills-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "review");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "SKILL.md"), "---\nname: review\ndescription: >\n  Review code carefully.\n---\n# Review\nCheck risks.", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, "helper.sh"), "exit 99", TestContext.Current.CancellationToken);
            var catalog = new LocalSkillCatalog([root]);
            catalog.Index.Single().Description.Trim().ShouldBe("Review code carefully.");
            var context = new ToolContext { Workspace = root };
            var content = await catalog.LoadAsync("review", context, TestContext.Current.CancellationToken);
            content!.Body.ShouldBe("# Review\nCheck risks.");
            content.Files.Single().ShouldBe("helper.sh");
            (await catalog.LoadAsync("../review", context, TestContext.Current.CancellationToken)).ShouldBeNull();
            await Should.ThrowAsync<IOException>(() => catalog.LoadAsync("review", new ToolContext { Workspace = root, Limits = new() { MaxFileBytes = 10 } }, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SessionChangesAndTodosPersistAndRelocationWaitsForBoundary()
    {
        var root = Path.Combine(Path.GetTempPath(), "ca-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new JsonlJournalStore(root);
            var id = Guid.NewGuid();
            var context = new ToolContext { SessionId = id, Workspace = root };
            await store.AppendAsync(new SessionStarted(id, 0, default, root, null), 0, TestContext.Current.CancellationToken);
            var control = new JournalSessionControl(store, Directory.Exists);
            await control.RenameAsync(id, "Work", context, TestContext.Current.CancellationToken);
            (await control.RelocateAsync(id, root, context, TestContext.Current.CancellationToken)).Pending.ShouldBeTrue();
            await new JournalTodoStore(store).ReplaceAsync(id, [new("Finish", TodoStatus.InProgress)], TestContext.Current.CancellationToken);
            var reopened = new JsonlJournalStore(root);
            var entries = await reopened.ReadAsync(id, TestContext.Current.CancellationToken);
            entries.OfType<SessionRenamed>().Single().Title.ShouldBe("Work");
            entries.OfType<SessionRelocated>().ShouldBeEmpty();
            entries.OfType<TodoUpdated>().Single().Items.Single().Status.ShouldBe("InProgress");
            await new JournalTodoStore(reopened).ReplaceAsync(id, [], TestContext.Current.CancellationToken);
            (await new JsonlJournalStore(root).ReadAsync(id, TestContext.Current.CancellationToken)).OfType<TodoUpdated>().Last().Items.ShouldBeEmpty();
            (await new JournalSessionControl(reopened, Directory.Exists).ApplyPendingAtBoundaryAsync(id, TestContext.Current.CancellationToken))!.Pending.ShouldBeFalse();
            (await control.ApplyPendingAtBoundaryAsync(id, TestContext.Current.CancellationToken)).ShouldBeNull();
            await Should.ThrowAsync<UnauthorizedAccessException>(() => control.RenameAsync(Guid.NewGuid(), "Wrong", context, TestContext.Current.CancellationToken));
            await Should.ThrowAsync<JournalConflictException>(() => store.AppendAsync(new SessionRenamed(id, 0, default, "stale"), 0, TestContext.Current.CancellationToken));
            await File.AppendAllTextAsync(Path.Combine(root, id.ToString("N") + ".jsonl"), "partial", TestContext.Current.CancellationToken);
            await Should.ThrowAsync<IOException>(() => store.ReadAsync(id, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }
}