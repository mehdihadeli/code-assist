using CodeAssist.Persistence;
using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Sessions;

public sealed class JournalTeamHub(IJournalStore journal) : ITeamHub
{
    public async Task<TeamRecord> CreateAsync(string name, ToolContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl)) throw new ArgumentException("Team name must fit 80 characters without controls.");
        await RequireSessionAsync(context.SessionId, cancellationToken);
        var team = new TeamRecord(Guid.NewGuid(), name.Trim(), context.SessionId, [context.SessionId], [], false, 1);
        await journal.AppendAsync(new TeamChanged(team.Id, 0, default, team), 0, cancellationToken);
        return team;
    }

    public async Task<TeamRecord> GetAsync(Guid team, ToolContext context, CancellationToken cancellationToken)
    {
        var (_, record) = await LoadAsync(team, context, cancellationToken);
        return record;
    }

    public async Task<TeamRecord> ChangeAsync(Guid team, int revision, TeamMutation mutation, Guid? member, ToolContext context, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(mutation)) throw new ArgumentException("Unknown team mutation.");
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var (entries, current) = await LoadAsync(team, context, cancellationToken);
            if (current.Closed) throw new InvalidOperationException("Team is closed.");
            if (current.Revision != revision) throw new InvalidOperationException("Team revision stale; read it again before updating.");
            var members = current.Members.ToList();
            var invitations = current.Invitations.ToList();
            var closed = false;
            if (mutation is TeamMutation.Invite or TeamMutation.Revoke or TeamMutation.Close)
            {
                if (current.Leader != context.SessionId) throw new UnauthorizedAccessException("Only the team leader may invite, revoke or close.");
                if (mutation == TeamMutation.Close)
                {
                    if (members.Count != 1) throw new InvalidOperationException("Every teammate must leave before the team closes.");
                    invitations.Clear();
                    closed = true;
                }
                else if (member is null || member == Guid.Empty) throw new ArgumentException("A real member session is required.");
                else if (mutation == TeamMutation.Invite)
                {
                    if (members.Contains(member.Value) || invitations.Contains(member.Value)) throw new InvalidOperationException("Session is already a member or invited.");
                    if (members.Count + invitations.Count >= 64) throw new InvalidOperationException("Teams are limited to 64 members and invitations.");
                    await RequireSessionAsync(member.Value, cancellationToken);
                    invitations.Add(member.Value);
                }
                else if (!invitations.Remove(member.Value)) throw new InvalidOperationException("Invitation does not exist.");
            }
            else if (mutation == TeamMutation.Join)
            {
                if (!invitations.Remove(context.SessionId)) throw new UnauthorizedAccessException("Joining requires an invitation for the current session.");
                members.Add(context.SessionId);
            }
            else
            {
                if (context.SessionId == current.Leader) throw new InvalidOperationException("Leader must close the team rather than leave.");
                if (!members.Remove(context.SessionId)) throw new UnauthorizedAccessException("Current session is not a member.");
            }
            var changed = current with { Members = members.ToArray(), Invitations = invitations.ToArray(), Closed = closed, Revision = checked(revision + 1) };
            try
            {
                await journal.AppendAsync(new TeamChanged(team, 0, default, changed), entries[^1].Seq, cancellationToken);
                return changed;
            }
            catch (JournalConflictException) when (attempt < 3) { }
        }
        throw new JournalConflictException();
    }

    public async Task<TeamLetter> SendAsync(Guid team, LetterDraft draft, ToolContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(draft.Topic) || draft.Topic.Length > 200 || draft.Topic.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(draft.Text) || draft.Text.Length > 16000 || draft.Broadcast == (draft.Recipient is not null) ||
            draft.Kind is not ("text" or "shutdown.request" or "shutdown.accept" or "shutdown.reject"))
            throw new ArgumentException("Supply a bounded topic/text and exactly one recipient or broadcast.");
        var id = Guid.NewGuid();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var (entries, current) = await LoadAsync(team, context, cancellationToken);
            RequireMember(current, context.SessionId);
            if (!string.IsNullOrWhiteSpace(context.CallId))
            {
                var previous = entries.OfType<TeamLetterSent>().SingleOrDefault(entry => entry.CallId == context.CallId && entry.Letter.Sender == context.SessionId)?.Letter;
                if (previous is not null)
                {
                    if (previous.Topic != draft.Topic.Trim() || previous.Text != draft.Text || previous.ReplyTo != draft.ReplyTo || previous.Kind != draft.Kind || previous.Broadcast != draft.Broadcast ||
                        !draft.Broadcast && (previous.Recipients.Count != 1 || previous.Recipients[0] != draft.Recipient)) throw new InvalidOperationException("Mail call ID was already used with different content.");
                    return previous;
                }
            }
            if (current.Closed) throw new InvalidOperationException("Team is closed.");
            if (entries.OfType<TeamLetterSent>().Count() >= 1000) throw new InvalidOperationException("Team mail history limit reached.");
            var recipients = draft.Broadcast ? current.Members.Where(session => session != context.SessionId).ToArray() : new[] { draft.Recipient!.Value };
            if (recipients.Length == 0 || recipients.Any(session => !current.Members.Contains(session))) throw new InvalidOperationException("Recipients must be joined team members.");
            if (draft.ReplyTo is not null && !entries.OfType<TeamLetterSent>().Any(entry => entry.Letter.Id == draft.ReplyTo &&
                (entry.Letter.Sender == context.SessionId || entry.Letter.Recipients.Contains(context.SessionId))))
                throw new UnauthorizedAccessException("Reply target is not visible to the current session.");
            if (draft.Kind == "shutdown.request" && (current.Leader != context.SessionId || draft.Broadcast || draft.Recipient == context.SessionId || draft.ReplyTo is not null))
                throw new UnauthorizedAccessException("Only the leader may request a specific teammate's shutdown.");
            if (draft.Kind is "shutdown.accept" or "shutdown.reject")
            {
                var request = entries.OfType<TeamLetterSent>().SingleOrDefault(entry => entry.Letter.Id == draft.ReplyTo)?.Letter;
                if (request is null || request.Kind != "shutdown.request" || !request.Recipients.Contains(context.SessionId) || draft.Broadcast || draft.Recipient != request.Sender)
                    throw new UnauthorizedAccessException("Shutdown responses must reply to the current session's request and target its sender.");
                if (entries.OfType<TeamLetterSent>().Any(entry => entry.Letter.ReplyTo == draft.ReplyTo && entry.Letter.Sender == context.SessionId && entry.Letter.Kind is "shutdown.accept" or "shutdown.reject"))
                    throw new InvalidOperationException("Shutdown request already has a response.");
            }
            var letter = new TeamLetter(id, context.SessionId, recipients, draft.Topic.Trim(), draft.Text, draft.ReplyTo, draft.Kind, draft.Broadcast);
            try
            {
                await journal.AppendAsync(new TeamLetterSent(team, 0, default, letter, string.IsNullOrWhiteSpace(context.CallId) ? null : context.CallId), entries[^1].Seq, cancellationToken);
                return letter;
            }
            catch (JournalConflictException) when (attempt < 3) { }
        }
        throw new JournalConflictException();
    }

    public async Task<MailPage> ReadAsync(Guid team, long after, int limit, ToolContext context, CancellationToken cancellationToken)
    {
        if (after < 0 || limit is < 1 or > 100) throw new ArgumentException("Mail cursor or page limit is invalid.");
        var (entries, current) = await LoadAsync(team, context, cancellationToken);
        RequireMember(current, context.SessionId);
        var acknowledged = entries.OfType<TeamLetterAcknowledged>().Where(entry => entry.Reader == context.SessionId).Select(entry => entry.LetterId).ToHashSet();
        var items = entries.OfType<TeamLetterSent>().Where(entry => entry.Seq > after && entry.Letter.Recipients.Contains(context.SessionId)).Take(limit)
            .Select(entry => new DeliveredLetter(entry.Seq, entry.At, entry.Letter, acknowledged.Contains(entry.Letter.Id))).ToArray();
        return new(items.LastOrDefault()?.Cursor ?? after, items);
    }

    public async Task AcknowledgeAsync(Guid team, Guid letter, ToolContext context, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var (entries, current) = await LoadAsync(team, context, cancellationToken);
            RequireMember(current, context.SessionId);
            if (!entries.OfType<TeamLetterSent>().Any(entry => entry.Letter.Id == letter && entry.Letter.Recipients.Contains(context.SessionId)))
                throw new UnauthorizedAccessException("Only a recipient may acknowledge the letter.");
            if (entries.OfType<TeamLetterAcknowledged>().Any(entry => entry.LetterId == letter && entry.Reader == context.SessionId)) return;
            try
            {
                await journal.AppendAsync(new TeamLetterAcknowledged(team, 0, default, letter, context.SessionId), entries[^1].Seq, cancellationToken);
                return;
            }
            catch (JournalConflictException) when (attempt < 3) { }
        }
        throw new JournalConflictException();
    }

    private async Task<(IReadOnlyList<JournalEntry> Entries, TeamRecord Team)> LoadAsync(Guid team, ToolContext context, CancellationToken cancellationToken)
    {
        if (team == Guid.Empty || context.SessionId == Guid.Empty) throw new UnauthorizedAccessException("Real team and session IDs are required.");
        var entries = await journal.ReadAsync(team, cancellationToken);
        if (entries.FirstOrDefault() is not TeamChanged first || first.Team.Id != team) throw new IOException("Team does not exist.");
        var current = entries.OfType<TeamChanged>().Last().Team;
        if (!current.Members.Contains(context.SessionId) && !current.Invitations.Contains(context.SessionId)) throw new UnauthorizedAccessException("Session is not a member or invitee.");
        return (entries, current);
    }

    private async Task RequireSessionAsync(Guid session, CancellationToken cancellationToken)
    {
        if (session == Guid.Empty || (await journal.ReadAsync(session, cancellationToken)).FirstOrDefault() is not SessionStarted)
            throw new InvalidOperationException("Team participation requires an existing session.");
    }

    private static void RequireMember(TeamRecord team, Guid session)
    {
        if (!team.Members.Contains(session)) throw new UnauthorizedAccessException("Mail requires joined team membership.");
    }
}