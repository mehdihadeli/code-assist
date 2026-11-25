using CodeAssist.Persistence;
using CodeAssist.Tools;
using CodeAssist.Tools.Execution;
using CodeAssist.Security;
using CodeAssist.Sessions;
using CodeAssist.Contracts;
using NSubstitute;
using Shouldly;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class PlanningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ca-plan-" + Guid.NewGuid().ToString("N"));
    private readonly ToolContext _context = new() { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() };
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private async Task<JournalPlanningSessions> StartAsync(IUserInteraction interaction)
    {
        var journal = new JsonlJournalStore(_root);
        await journal.AppendAsync(new SessionStarted(_context.SessionId, 0, default, _context.Workspace, null), 0, Token);
        return new JournalPlanningSessions(journal, interaction);
    }

    [Fact]
    public async Task Should_RemainRestrictedUntilUserApproves_When_PlanIsSubmitted()
    {
        var broker = new QuestionBroker();
        var planning = await StartAsync(broker);
        var entered = await planning.EnterAsync(_context, Token);
        entered.Active.ShouldBeTrue();
        (await planning.EnterAsync(_context, Token)).Revision.ShouldBe(entered.Revision);
        (await planning.RequestApplyAsync("Add tests", _context, Token)).Active.ShouldBeTrue();
        broker.Requested += request => broker.Answer(_context.SessionId, request.Id, [new QuestionAnswer(["Keep planning"], null)]);
        (await planning.RequestApplyAsync("Add tests", _context, Token)).Active.ShouldBeTrue();
        var approving = new QuestionBroker();
        approving.Requested += request => approving.Answer(_context.SessionId, request.Id, [new QuestionAnswer(["Apply"], null)]);
        var reopened = new JournalPlanningSessions(new JsonlJournalStore(_root), approving);
        (await reopened.RequestApplyAsync("Add tests", _context, Token)).Active.ShouldBeFalse();
        (await new JournalPlanningSessions(new JsonlJournalStore(_root), broker).ReadAsync(_context, Token)).Active.ShouldBeFalse();
        (await new JsonlJournalStore(_root).ReadAsync(_context.SessionId, Token)).OfType<PlanningChanged>().Last().ApprovedProposal.ShouldBe("Add tests");
    }

    [Fact]
    public async Task Should_RejectMutationAndPreserveUnderlyingPolicy_When_PlanningGuardIsApplied()
    {
        var planning = await StartAsync(new QuestionBroker());
        await planning.EnterAsync(_context, Token);
        var underlying = Substitute.For<IToolAuthorization>();
        underlying.AuthorizeAsync(Arg.Any<ToolSpec>(), Arg.Any<ToolPlan>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(new ToolAuthorization(true));
        var guard = new PlanningAuthorization(underlying, planning);
        var spec = new ToolSpec("custom", "test", "{}", ToolRisk.Read, ["read"]);
        foreach (var kind in new[] { ToolEffectKind.Write, ToolEffectKind.Delete, ToolEffectKind.Exec, ToolEffectKind.Net })
            (await guard.AuthorizeAsync(spec, new([new(kind, "target")]), _context, Token)).Allowed.ShouldBeFalse();
        (await guard.AuthorizeAsync(spec with { Name = "session.plan" }, new([new(ToolEffectKind.Exec, "process")]), _context, Token)).Allowed.ShouldBeFalse();
        (await guard.AuthorizeAsync(spec, new([new(ToolEffectKind.Read, "file")]), _context, Token)).Allowed.ShouldBeTrue();
        underlying.AuthorizeAsync(Arg.Any<ToolSpec>(), Arg.Any<ToolPlan>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(new ToolAuthorization(false, "base denied"));
        (await guard.AuthorizeAsync(spec, new([new(ToolEffectKind.Read, "file")]), _context, Token)).Allowed.ShouldBeFalse();
        var missing = new ToolContext { Workspace = _context.Workspace, SessionId = Guid.NewGuid() };
        (await guard.AuthorizeAsync(spec, new([]), missing, Token)).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Should_InvalidateStaleReview_When_PlanningStateChangesDuringApproval()
    {
        var broker = new QuestionBroker();
        var planning = await StartAsync(broker);
        await planning.EnterAsync(_context, Token);
        var requested = new TaskCompletionSource<PendingQuestion>(TaskCreationOptions.RunContinuationsAsynchronously);
        broker.Requested += request => requested.TrySetResult(request);
        var pending = planning.RequestApplyAsync("Old plan", _context, Token);
        var request = await requested.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var journal = new JsonlJournalStore(_root);
        var entries = await journal.ReadAsync(_context.SessionId, Token);
        await journal.AppendAsync(new PlanningChanged(_context.SessionId, 0, default, true, null), entries[^1].Seq, Token);
        broker.Answer(_context.SessionId, request.Id, [new QuestionAnswer(["Apply"], null)]).ShouldBeTrue();
        await Should.ThrowAsync<InvalidOperationException>(() => pending);
        (await planning.ReadAsync(_context, Token)).Active.ShouldBeTrue();
    }

    [Fact]
    public async Task Should_RejectModelSuppliedApproval_When_PlanningToolArgumentsAreInvalid()
    {
        var tool = new SessionPlanTool(await StartAsync(new QuestionBroker()));
        await Should.ThrowAsync<ToolArgumentException>(() => tool.ExecuteAsync(ToolArguments.Parse("""{"operation":"submit","proposal":" "}"""), _context, Token));
        var authorization = Substitute.For<IToolAuthorization>();
        var dispatcher = new ToolDispatcher(_ => new ToolCatalog([tool]), authorization);
        var fabricated = await dispatcher.InvokeAsync("session.plan", """{"operation":"submit","proposal":"Run tests","approved":true}""", _context, Token);
        fabricated.IsError.ShouldBeTrue();
        await authorization.DidNotReceive().AuthorizeAsync(Arg.Any<ToolSpec>(), Arg.Any<ToolPlan>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>());
        var plan = await tool.PlanAsync(ToolArguments.Parse("""{"operation":"submit","proposal":"Run tests"}"""), _context, Token);
        plan.Effects.ShouldContain(effect => effect.Kind == ToolEffectKind.Ask);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => tool.ExecuteAsync(ToolArguments.Parse("""{"operation":"enter"}"""), _context, cancelled.Token));
    }
}