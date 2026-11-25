using CodeAssist.Contracts;
namespace CodeAssist.Persistence;

public sealed record WorktreeChanged(Guid SessionId, long Seq, DateTimeOffset At, SessionWorktree State) : JournalEntry(SessionId, Seq, At);