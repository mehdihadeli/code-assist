using CodeAssist.Contracts;
namespace CodeAssist.Persistence;

public sealed record AgentMessageRecorded(Guid SessionId, long Seq, DateTimeOffset At, string Role, string Text, IReadOnlyList<AgentCall> Calls, string? CallId = null) : JournalEntry(SessionId, Seq, At);