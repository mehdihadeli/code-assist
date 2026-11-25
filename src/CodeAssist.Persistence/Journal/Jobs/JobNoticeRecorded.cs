using CodeAssist.Contracts;
namespace CodeAssist.Persistence;

public sealed record JobNoticeRecorded(Guid SessionId, long Seq, DateTimeOffset At, Guid JobId, string Description, string Status, string Text, Guid? OutputId) : JournalEntry(SessionId, Seq, At);