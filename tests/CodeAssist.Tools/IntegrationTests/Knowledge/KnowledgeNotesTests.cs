using CodeAssist.Tools;
using CodeAssist.Sessions;
using NSubstitute;
using Shouldly;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class KnowledgeNotesTests
{
    [Fact]
    public async Task Should_EnforceScopesRevisionsAndConsent_When_PersistingKnowledge()
    {
        var root = Path.Combine(Path.GetTempPath(), "ca-knowledge-" + Guid.NewGuid().ToString("N"));
        try
        {
            var token = TestContext.Current.CancellationToken;
            var store = new FileKnowledgeNotes(root);
            var context = new ToolContext { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() };
            var note = await store.PutAsync("session", null, 0, "Build", "Use the focused checks.", context, token);
            var reopened = new FileKnowledgeNotes(root);
            (await reopened.ListAsync("session", context, token)).Single().Body.ShouldBe(note.Body);
            (await reopened.ListAsync("session", new ToolContext { Workspace = context.Workspace, SessionId = Guid.NewGuid() }, token)).ShouldBeEmpty();
            await Should.ThrowAsync<InvalidOperationException>(() => reopened.PutAsync("session", note.Id, 0, "Wrong", "Stale", context, token));
            var updated = await reopened.PutAsync("session", note.Id, 1, "Build", "Updated", context, token);
            updated.Revision.ShouldBe(2);
            await Should.ThrowAsync<InvalidOperationException>(() => reopened.RemoveAsync("session", note.Id, 1, context, token));
            await reopened.RemoveAsync("session", note.Id, 2, context, token);
            (await store.ListAsync("session", context, token)).ShouldBeEmpty();
            await store.PutAsync("workspace", null, 0, "Local", "Workspace only", context, token);
            (await store.ListAsync("workspace", new ToolContext { Workspace = Path.Combine(context.Workspace, "another-project"), SessionId = context.SessionId }, token)).ShouldBeEmpty();
            var interaction = Substitute.For<IUserInteraction>();
            var tool = new KnowledgeNotesTool(store, interaction);
            var refused = await tool.ExecuteAsync(ToolArguments.Parse("""{"operation":"put","scope":"user","revision":0,"title":"Shared","body":"Data"}"""), context, token);
            refused.IsError.ShouldBeTrue();
            (await store.ListAsync("user", context, token)).ShouldBeEmpty();
            interaction.AskAsync(Arg.Any<IReadOnlyList<Question>>(), context, token).Returns([new QuestionAnswer(["Allow"], null)]);
            (await tool.ExecuteAsync(ToolArguments.Parse("""{"operation":"put","scope":"user","revision":0,"title":"Shared","body":"Data"}"""), context, token)).IsError.ShouldBeFalse();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}