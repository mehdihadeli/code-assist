using CodeAssist.Persistence;
using CodeAssist.Tools;
using CodeAssist.Sessions;
using CodeAssist.Contracts;
using Shouldly;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class TeamToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ca-team-" + Guid.NewGuid().ToString("N"));
    private readonly ToolContext _leader = new() { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() };
    private readonly ToolContext _member = new() { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() };
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private JsonlJournalStore Journal => new(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task Should_ReplayMailAndValidateShutdownResponses_When_CoordinatingMembers()
    {
        var hub = await StartAsync();
        var team = await JoinAsync(hub);
        var leader = new ToolContext { Workspace = _leader.Workspace, SessionId = _leader.SessionId, CallId = "shutdown-request" };
        var member = new ToolContext { Workspace = _member.Workspace, SessionId = _member.SessionId, CallId = "shutdown-response" };
        var draft = new LetterDraft(member.SessionId, false, "Finish review", "Please finish and leave.", Kind: "shutdown.request");
        var request = await hub.SendAsync(team.Id, draft, leader, Token);
        (await hub.SendAsync(team.Id, draft, leader, Token)).Id.ShouldBe(request.Id);
        await Should.ThrowAsync<InvalidOperationException>(() => hub.SendAsync(team.Id, draft with { Text = "different" }, leader, Token));
        await Should.ThrowAsync<UnauthorizedAccessException>(() => hub.SendAsync(team.Id, new(leader.SessionId, false, "Fake", "No", Kind: "shutdown.request"), member, Token));
        await hub.SendAsync(team.Id, new(leader.SessionId, false, "Accepted", "Finishing", request.Id, "shutdown.accept"), member, Token);
        await Should.ThrowAsync<InvalidOperationException>(() => hub.ChangeAsync(team.Id, team.Revision, TeamMutation.Close, null, leader, Token));
        team = await hub.ChangeAsync(team.Id, team.Revision, TeamMutation.Leave, null, member, Token);
        (await hub.ChangeAsync(team.Id, team.Revision, TeamMutation.Close, null, leader, Token)).Closed.ShouldBeTrue();
    }

    private async Task<JournalTeamHub> StartAsync()
    {
        foreach (var context in new[] { _leader, _member })
            await Journal.AppendAsync(new SessionStarted(context.SessionId, 0, default, context.Workspace, null), 0, Token);
        return new JournalTeamHub(Journal);
    }

    private async Task<TeamRecord> JoinAsync(JournalTeamHub hub)
    {
        var team = await hub.CreateAsync("Review", _leader, Token);
        team = await hub.ChangeAsync(team.Id, team.Revision, TeamMutation.Invite, _member.SessionId, _leader, Token);
        return await hub.ChangeAsync(team.Id, team.Revision, TeamMutation.Join, null, _member, Token);
    }

    [Fact]
    public async Task Should_RequireInvitationsAndLeaderAuthority_When_TeamMembershipChanges()
    {
        var hub = await StartAsync();
        var team = await hub.CreateAsync("Review", _leader, Token);
        await Should.ThrowAsync<UnauthorizedAccessException>(() => hub.GetAsync(team.Id, _member, Token));
        await Should.ThrowAsync<UnauthorizedAccessException>(() => hub.ChangeAsync(team.Id, 1, TeamMutation.Join, null, _member, Token));
        team = await hub.ChangeAsync(team.Id, 1, TeamMutation.Invite, _member.SessionId, _leader, Token);
        await Should.ThrowAsync<UnauthorizedAccessException>(() => hub.SendAsync(team.Id, new(_leader.SessionId, false, "Hi", "Not joined"), _member, Token));
        team = await hub.ChangeAsync(team.Id, team.Revision, TeamMutation.Join, null, _member, Token);
        await Should.ThrowAsync<UnauthorizedAccessException>(() => hub.ChangeAsync(team.Id, team.Revision, TeamMutation.Close, null, _member, Token));
        await Should.ThrowAsync<InvalidOperationException>(() => hub.ChangeAsync(team.Id, team.Revision, TeamMutation.Close, null, _leader, Token));
        await Should.ThrowAsync<InvalidOperationException>(() => hub.ChangeAsync(team.Id, 1, TeamMutation.Leave, null, _member, Token));
        team = await hub.ChangeAsync(team.Id, team.Revision, TeamMutation.Leave, null, _member, Token);
        team = await hub.ChangeAsync(team.Id, team.Revision, TeamMutation.Close, null, _leader, Token);
        (await new JournalTeamHub(Journal).GetAsync(team.Id, _leader, Token)).Closed.ShouldBeTrue();
        await Should.ThrowAsync<InvalidOperationException>(() => hub.SendAsync(team.Id, new(_leader.SessionId, false, "No", "Closed"), _leader, Token));
    }

    [Fact]
    public async Task Should_RecoverPrivateMailAndIdempotentReceipts_When_HubIsReopened()
    {
        var hub = await StartAsync();
        var team = await JoinAsync(hub);
        var sent = await hub.SendAsync(team.Id, new(_member.SessionId, false, "Review", "Untrusted body"), _leader, Token);
        var reopened = new JournalTeamHub(Journal);
        (await reopened.ReadAsync(team.Id, 0, 10, _leader, Token)).Items.ShouldBeEmpty();
        var page = await reopened.ReadAsync(team.Id, 0, 1, _member, Token);
        page.Items.ShouldHaveSingleItem().Letter.Id.ShouldBe(sent.Id);
        page.Items[0].Letter.Sender.ShouldBe(_leader.SessionId);
        page.Items[0].Acknowledged.ShouldBeFalse();
        (await reopened.ReadAsync(team.Id, page.NextCursor, 1, _member, Token)).Items.ShouldBeEmpty();
        await Should.ThrowAsync<UnauthorizedAccessException>(() => reopened.AcknowledgeAsync(team.Id, sent.Id, _leader, Token));
        await reopened.AcknowledgeAsync(team.Id, sent.Id, _member, Token);
        var sequence = (await Journal.ReadAsync(team.Id, Token)).Last().Seq;
        await reopened.AcknowledgeAsync(team.Id, sent.Id, _member, Token);
        (await Journal.ReadAsync(team.Id, Token)).Last().Seq.ShouldBe(sequence);
        (await reopened.ReadAsync(team.Id, 0, 1, _member, Token)).Items[0].Acknowledged.ShouldBeTrue();
    }

    [Fact]
    public async Task Should_ResolveBroadcastAndValidateReplyVisibility_When_MailIsSent()
    {
        var hub = await StartAsync();
        var team = await JoinAsync(hub);
        var sent = await hub.SendAsync(team.Id, new(null, true, "Checks", "Done"), _leader, Token);
        sent.Recipients.ShouldBe([_member.SessionId]);
        var reply = await hub.SendAsync(team.Id, new(_leader.SessionId, false, "Re: Checks", "Received", sent.Id), _member, Token);
        reply.ReplyTo.ShouldBe(sent.Id);
        await Should.ThrowAsync<UnauthorizedAccessException>(() => hub.SendAsync(team.Id, new(_leader.SessionId, false, "Unknown", "Reply", Guid.NewGuid()), _member, Token));
        await Should.ThrowAsync<InvalidOperationException>(() => hub.SendAsync(team.Id, new(Guid.NewGuid(), false, "Unknown", "Recipient"), _leader, Token));
    }

    [Fact]
    public async Task Should_ValidateToolArgumentsAndDeclareMailEffects_When_ToolsArePlanned()
    {
        var hub = await StartAsync();
        var team = await JoinAsync(hub);
        var manage = new TeamManageTool(hub);
        var mail = new TeamMailTool(hub);
        var read = ToolArguments.Parse($$"""{"operation":"read","team":"{{team.Id}}"}""");
        (await mail.PlanAsync(read, _leader, Token)).Effects.ShouldAllBe(effect => effect.Kind == ToolEffectKind.Read);
        (await mail.ExecuteAsync(read, _leader, Token)).IsError.ShouldBeFalse();
        await Should.ThrowAsync<ToolArgumentException>(() => manage.ExecuteAsync(ToolArguments.Parse($$"""{"operation":"join","team":"{{team.Id}}","revision":3,"member":"{{_leader.SessionId}}"}"""), _member, Token));
        await Should.ThrowAsync<ToolArgumentException>(() => mail.ExecuteAsync(ToolArguments.Parse($$"""{"operation":"send","team":"{{team.Id}}","topic":"Hi","text":"Missing target"}"""), _leader, Token));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => hub.SendAsync(team.Id, new(_member.SessionId, false, "Cancelled", "No delivery"), _leader, cancelled.Token));
        (await hub.ReadAsync(team.Id, 0, 100, _member, Token)).Items.ShouldBeEmpty();
    }
}