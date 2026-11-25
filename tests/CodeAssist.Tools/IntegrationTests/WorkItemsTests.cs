using CodeAssist.Persistence;
using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Sessions;
using CodeAssist.Contracts;
using Shouldly;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class WorkItemsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ca-work-" + Guid.NewGuid().ToString("N"));
    private readonly ToolContext _context = new() { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() };
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private JsonlJournalStore Journal => new(_root);

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private async Task<JournalWorkLedger> StartAsync()
    {
        await Journal.AppendAsync(new SessionStarted(_context.SessionId, 0, default, _context.Workspace, null), 0, Token);
        return new JournalWorkLedger(Journal);
    }

    [Fact]
    public async Task Should_RecoverWorkAndRejectStaleRevisions_When_LedgerIsReopened()
    {
        var ledger = await StartAsync();
        var first = await ledger.CreateAsync(new("First", "Detail", "builder", []), _context, Token);
        var done = await ledger.ReplaceAsync(first.Id, first.Revision, new("First", "Done", null, []), WorkState.Done, _context, Token);
        var reopened = new JournalWorkLedger(Journal);
        var recovered = (await reopened.ListAsync(_context, Token)).ShouldHaveSingleItem();
        recovered.State.ShouldBe(WorkState.Done);
        recovered.Revision.ShouldBe(2);
        recovered.Owner.ShouldBeNull();
        await Should.ThrowAsync<InvalidOperationException>(() => reopened.ReplaceAsync(first.Id, first.Revision, new("Stale", "", null, []), WorkState.Queued, _context, Token));
        await reopened.RemoveAsync(done.Id, done.Revision, _context, Token);
        (await new JournalWorkLedger(Journal).ListAsync(_context, Token)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Should_RejectCyclesBlockedExecutionAndUnsafeRemoval_When_WorkHasPrerequisites()
    {
        var ledger = await StartAsync();
        var first = await ledger.CreateAsync(new("First", "", null, []), _context, Token);
        var second = await ledger.CreateAsync(new("Second", "", null, [first.Id]), _context, Token);
        await Should.ThrowAsync<InvalidOperationException>(() => ledger.ReplaceAsync(first.Id, 1, new("First", "", null, [second.Id]), WorkState.Queued, _context, Token));
        await Should.ThrowAsync<InvalidOperationException>(() => ledger.ReplaceAsync(second.Id, 1, new("Second", "", null, [first.Id]), WorkState.Active, _context, Token));
        await Should.ThrowAsync<InvalidOperationException>(() => ledger.RemoveAsync(first.Id, 1, _context, Token));
        await ledger.ReplaceAsync(first.Id, 1, new("First", "", null, []), WorkState.Done, _context, Token);
        await ledger.ReplaceAsync(second.Id, 1, new("Second", "", null, [first.Id]), WorkState.Active, _context, Token);
        await Should.ThrowAsync<InvalidOperationException>(() => ledger.ReplaceAsync(first.Id, 2, new("First", "", null, []), WorkState.Queued, _context, Token));
        await Should.ThrowAsync<InvalidOperationException>(() => ledger.RemoveAsync(second.Id, 2, _context, Token));
    }

    [Fact]
    public async Task Should_IsolateSessionsAndHonorCancellation_When_WorkIsAccessed()
    {
        var ledger = await StartAsync();
        var item = await ledger.CreateAsync(new("Private", "", null, []), _context, Token);
        var other = new ToolContext { Workspace = _context.Workspace, SessionId = Guid.NewGuid() };
        await Journal.AppendAsync(new SessionStarted(other.SessionId, 0, default, other.Workspace, null), 0, Token);
        (await ledger.ListAsync(other, Token)).ShouldBeEmpty();
        await Should.ThrowAsync<InvalidOperationException>(() => ledger.RemoveAsync(item.Id, 1, other, Token));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => ledger.CreateAsync(new("Cancelled", "", null, []), _context, cancelled.Token));
        (await ledger.ListAsync(_context, Token)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Should_CreateReadPageAndDeclareEffects_When_WorkToolIsInvoked()
    {
        var tool = new WorkItemsTool(await StartAsync());
        var created = await tool.ExecuteAsync(ToolArguments.Parse("""{"operation":"create","title":"Build","detail":"Run checks"}"""), _context, Token);
        created.IsError.ShouldBeFalse(created.Text);
        using var json = JsonDocument.Parse(created.Text);
        var id = json.RootElement.GetProperty("id").GetGuid();
        var get = ToolArguments.Parse(JsonSerializer.Serialize(new { operation = "get", id }));
        (await tool.PlanAsync(get, _context, Token)).Effects.ShouldAllBe(effect => effect.Kind == ToolEffectKind.Read);
        (await tool.ExecuteAsync(get, _context, Token)).Text.ShouldContain("Run checks");
        var page = await tool.ExecuteAsync(ToolArguments.Parse("""{"operation":"list","limit":1}"""), _context, Token);
        page.Text.ShouldContain("\"total\":1");
        await Should.ThrowAsync<ToolArgumentException>(() => tool.ExecuteAsync(ToolArguments.Parse("""{"operation":"replace","id":"bad"}"""), _context, Token));
    }
}