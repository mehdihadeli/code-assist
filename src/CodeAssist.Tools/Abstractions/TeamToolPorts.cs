using CodeAssist.Persistence;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public enum TeamMutation { Invite, Revoke, Join, Leave, Close }

public sealed record LetterDraft(Guid? Recipient, bool Broadcast, string Topic, string Text, Guid? ReplyTo = null, string Kind = "text");
public sealed record DeliveredLetter(long Cursor, DateTimeOffset At, TeamLetter Letter, bool Acknowledged);
public sealed record MailPage(long NextCursor, IReadOnlyList<DeliveredLetter> Items);

public interface ITeamHub
{
    Task<TeamRecord> CreateAsync(string name, ToolContext context, CancellationToken cancellationToken);
    Task<TeamRecord> GetAsync(Guid team, ToolContext context, CancellationToken cancellationToken);
    Task<TeamRecord> ChangeAsync(Guid team, int revision, TeamMutation mutation, Guid? member, ToolContext context, CancellationToken cancellationToken);
    Task<TeamLetter> SendAsync(Guid team, LetterDraft draft, ToolContext context, CancellationToken cancellationToken);
    Task<MailPage> ReadAsync(Guid team, long after, int limit, ToolContext context, CancellationToken cancellationToken);
    Task AcknowledgeAsync(Guid team, Guid letter, ToolContext context, CancellationToken cancellationToken);
}