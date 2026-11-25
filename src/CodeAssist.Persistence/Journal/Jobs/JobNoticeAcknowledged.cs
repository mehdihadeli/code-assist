using CodeAssist.Contracts;
namespace CodeAssist.Persistence;

public sealed record JobNoticeAcknowledged(Guid SessionId, long Seq, DateTimeOffset At, Guid JobId) : JournalEntry(SessionId, Seq, At);